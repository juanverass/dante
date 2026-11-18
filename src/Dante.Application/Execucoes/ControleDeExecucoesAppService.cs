using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Cancelamento durável pelo proprietário (#305) sobre o ledger e a fila (AD-66/67): nenhum estado paralelo, e quem não
// tem a posse de um trabalho reivindicado nunca altera execução, etapa ou trabalho.
public sealed class ControleDeExecucoesAppService(
    IFilaDeTrabalho fila,
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IEventoDeOrquestracaoRepository eventos,
    IUnitOfWorkDaOrquestracao unidade,
    CanalDeCancelamento canal,
    IReservaDeWorktreeRepository reservas,
    IPedidoDeAprovacaoRepository pedidos,
    IDelegacaoRepository delegacoes) : IControleDeExecucoesAppService
{
    public const string MotivoDoDescarte = "cancelada pelo dono";

    public async Task<CancelamentoDeExecucaoDto> SolicitarCancelamentoAsync(ProprietarioDaExecucao proprietario,
        string codigo, string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        var naoEncontrada = new CancelamentoDeExecucaoDto(ResultadoDoCancelamento.NaoEncontrada);
        if (!Execucao.TentarInterpretarCodigo(codigo, out var numero, out var avulsa)) return naoEncontrada;
        // Leitura sem rastreamento só para localizar e autorizar; o estado decisivo é relido com as linhas travadas.
        var encontrada = await execucoes.ConsultarPorNumeroAsync(numero, cancellationToken);
        if (encontrada is null || !proprietario.EDono(encontrada.Canal, encontrada.IdDoUsuarioNoCanal) ||
            (encontrada.Tipo == TipoDeExecucao.Avulsa) != avulsa)
            return naoEncontrada;
        var motivoSeguro = PedidoDeCancelamento.MotivoSeguro(motivo);
        CancelamentoDeExecucaoDto resultado = naoEncontrada;
        await fila.ExecutarEmTransacaoAsync(async token =>
        {
            // Trabalho antes da execução: mesma ordem de GravarComPosseAsync (trava o trabalho, depois grava a execução).
            // Uma subordinada (#309) trava a origem antes de si mesma, a ordem única entre pai e filha.
            var ativos = await fila.TravarAtivosDaExecucaoAsync(encontrada.Id, token);
            var execucao = await execucoes.TravarComAncestraisAsync(encontrada.Id, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            if (execucao.Terminal)
            {
                resultado = new(ResultadoDoCancelamento.JaEncerrada, execucao.Codigo, execucao.Estado);
                return;
            }
            var jaPedido = (await eventos.ConsultarDoTipoAsync([execucao.Id],
                TipoDeEventoDeOrquestracao.CancelamentoSolicitado, token)).Count > 0;
            var comDono = ativos.Any(t => t.Estado == EstadoDoTrabalho.Reivindicado) ||
                (ativos.Count == 0 && execucao.Estado == EstadoDaExecucao.EmExecucao);
            if (comDono)
            {
                if (execucao.Tipo == TipoDeExecucao.Avulsa)
                    resultado = new(ResultadoDoCancelamento.JobEmAndamento, execucao.Codigo, execucao.Estado);
                else if (jaPedido)
                    resultado = new(ResultadoDoCancelamento.JaSolicitada, execucao.Codigo, execucao.Estado);
                else
                {
                    // Só o pedido: append-only, não toca a linha da execução (o xmin de quem tem a posse não muda).
                    await eventos.AdicionarAsync(Pedido(execucao, proprietario, motivoSeguro, fila.Agora), token);
                    await unidade.SalvarAlteracoesAsync(token);
                    resultado = new(ResultadoDoCancelamento.Solicitada, execucao.Codigo, execucao.Estado);
                }
                return;
            }
            // Ninguém tem a posse: as linhas travadas impedem a reivindicação até o commit, e o cancelamento é direto.
            var agora = fila.Agora;
            var motivoDaTransicao = PedidoDeCancelamento.MotivoDaTransicao(motivoSeguro);
            if (!jaPedido) await eventos.AdicionarAsync(Pedido(execucao, proprietario, motivoSeguro, agora), token);
            foreach (var etapa in (await etapas.ListarDaExecucaoAsync(execucao.Id, token)).Where(e => !e.Terminal))
            {
                var antes = etapa.Estado;
                etapa.Cancelar(motivoDaTransicao, null, agora);
                await eventos.AdicionarAsync(Transicao(execucao, etapa, antes, etapa.Estado, agora), token);
            }
            // Roteiro esperando a decisão humana (#308): o pedido pendente termina cancelado, e nenhuma decisão posterior
            // vale para ele.
            if (await pedidos.ObterPendenteAsync(execucao.Id, token) is { } pendente)
                pendente.Cancelar(motivoDaTransicao, agora);
            var execucaoAntes = execucao.Estado;
            execucao.Cancelar(motivoDaTransicao, agora);
            await eventos.AdicionarAsync(Transicao(execucao, null, execucaoAntes, execucao.Estado, agora), token);
            foreach (var trabalho in ativos)
                if (!await fila.DescartarPendenteAsync(trabalho.Id, MotivoDoDescarte, token))
                    throw new ConflitoDeConcorrenciaDaFila();
            // Interrompida com worktree (escrita, #307): a reserva deixa de ocupar capacidade; a limpeza preserva o
            // diretório se houver alterações.
            await LiberacaoDeWorktrees.LiberarSeEncerradaAsync(reservas, eventos, execucao, agora, token);
            // Delegações (#309): a da subordinada termina Cancelada e a origem pode ser retomada; uma origem cancelada pede o
            // cancelamento das subordinadas abertas.
            await CicloDaDelegacao.AoEncerrarAsync(new(delegacoes, execucoes, etapas, eventos, fila), execucao, agora, token);
            await unidade.SalvarAlteracoesAsync(token);
            resultado = new(ResultadoDoCancelamento.Cancelada, execucao.Codigo, execucao.Estado);
        }, cancellationToken);
        // Depois do commit: o pedido já é durável; o sinal só acelera quem tem a posse neste processo.
        if (resultado.Resultado == ResultadoDoCancelamento.Solicitada) canal.Sinalizar(numero);
        return resultado;
    }

    private static EventoDeOrquestracao Pedido(Execucao execucao, ProprietarioDaExecucao autor, string? motivo,
        DateTimeOffset quando) =>
        EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.CancelamentoSolicitado,
            PedidoDeCancelamento.Dados(autor, motivo), quando);

    // Mesmo formato de evento de transição do ledger (AD-66).
    private static EventoDeOrquestracao Transicao(Execucao execucao, EtapaDaExecucao? etapa, Enum de, Enum para,
        DateTimeOffset quando) =>
        EventoDeOrquestracao.DaExecucao(execucao,
            etapa is null ? TipoDeEventoDeOrquestracao.ExecucaoTransitou : TipoDeEventoDeOrquestracao.EtapaTransitou,
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["de"] = de.ToString(),
                ["para"] = para.ToString()
            }), quando, etapa);

    // Um trabalho travado como pendente deixou de estar pendente na mesma transação: não deveria acontecer; nada é
    // gravado (a transação inteira é desfeita).
    private sealed class ConflitoDeConcorrenciaDaFila()
        : InvalidOperationException("O trabalho da execução mudou de estado durante o cancelamento.");
}
