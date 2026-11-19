using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Fila durável sobre o ledger da #299 (AD-67): as transições continuam nos métodos do domínio e o registro do job
// legado continua no ILedgerDeExecucoesAppService; aqui elas passam a acontecer sob posse do trabalho, na mesma
// transação do trabalho que as originou.
public sealed class FilaDeExecucoesAppService(
    IFilaDeTrabalho fila,
    ILedgerDeExecucoesAppService ledger,
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IDependenciaDeEtapaRepository dependencias,
    IEventoDeOrquestracaoRepository eventos,
    IUnitOfWorkDaOrquestracao unidade,
    ISessaoDeAgenteRepository sessoes,
    IReservaDeWorktreeRepository reservas,
    IDelegacaoRepository delegacoes) : IFilaDeExecucoesAppService
{
    public const string MotivoDeResultadoIncerto =
        "Resultado incerto: a etapa foi interrompida durante uma ação externa e não é repetida automaticamente. " +
        "Confira o efeito antes de continuar.";

    public async Task<JobNaFilaDto> EnfileirarJobAsync(JobRegistradoDto job, string chaveDeDeduplicacao,
        CancellationToken cancellationToken = default, IdentidadeDoProcessador? origem = null)
    {
        LedgerDeExecucoesValidator.Validar(job);
        ValidarChave(chaveDeDeduplicacao);
        if (await ObterJobPorChaveAsync(chaveDeDeduplicacao, cancellationToken) is { } existente) return existente;
        var idExecucao = Guid.Empty;
        await fila.ExecutarEmTransacaoAsync(async token =>
        {
            await ledger.RegistrarJobCriadoAsync(job, token);
            var execucao = await execucoes.ObterPorNumeroAsync(job.Numero, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            var etapa = (await etapas.ListarDaExecucaoAsync(execucao.Id, token)).Single(e => e.Numero == 1);
            await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, etapa, chaveDeDeduplicacao, fila.Agora,
                origem: origem?.Origem), token);
            await unidade.SalvarAlteracoesAsync(token);
            idExecucao = execucao.Id;
        }, cancellationToken);
        return new JobNaFilaDto(job.Numero, idExecucao, true);
    }

    public async Task<ExecucaoNaFilaDto> EnfileirarTarefaAsync(PlanoDaTarefa plano, long numero, string canal,
        long idDoUsuarioNoCanal, string chaveDeDeduplicacao, CancellationToken cancellationToken = default,
        IdentidadeDoProcessador? origem = null)
    {
        ArgumentNullException.ThrowIfNull(plano);
        ValidarChave(chaveDeDeduplicacao);
        if (await fila.ObterPorChaveAsync(chaveDeDeduplicacao, cancellationToken) is { IdExecucao: { } idExistente })
        {
            var existente = await execucoes.ObterPorIdAsync(idExistente, cancellationToken) ??
                throw new InvalidOperationException("Execução do trabalho não registrada no ledger.");
            return new ExecucaoNaFilaDto(existente.Numero, existente.Codigo, existente.Id, false);
        }
        var agora = fila.Agora;
        // Somente leitura: o domínio e a montagem recusam etapa de escrita antes de qualquer gravação.
        var (execucao, novas) = MontagemDaTarefa.CriarExecucao(plano, numero, canal, idDoUsuarioNoCanal, agora);
        await execucoes.AdicionarAsync(execucao, cancellationToken);
        foreach (var etapa in novas) await etapas.AdicionarAsync(etapa, cancellationToken);
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.ExecucaoCriada,
            JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["tipo"] = execucao.Tipo.ToString(),
                ["agente"] = plano.Handle,
                ["versao"] = plano.Versao.Numero.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }), agora), cancellationToken);
        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, novas[0], chaveDeDeduplicacao, agora,
            origem: origem?.Origem), cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return new ExecucaoNaFilaDto(execucao.Numero, execucao.Codigo, execucao.Id, true);
    }

    public async Task<JobNaFilaDto?> ObterJobPorChaveAsync(string chaveDeDeduplicacao,
        CancellationToken cancellationToken = default)
    {
        ValidarChave(chaveDeDeduplicacao);
        if (await fila.ObterPorChaveAsync(chaveDeDeduplicacao, cancellationToken) is not { IdExecucao: { } id }) return null;
        var execucao = await execucoes.ObterPorIdAsync(id, cancellationToken) ??
            throw new InvalidOperationException("Execução do trabalho não registrada no ledger.");
        return new JobNaFilaDto(execucao.Numero, execucao.Id, false);
    }

    public async Task<AvaliacaoDoTrabalhoDto> AvaliarAsync(TrabalhoReivindicado trabalho,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trabalho);
        if (trabalho.Tipo != TipoDeTrabalho.AvancarExecucao || trabalho.IdExecucao is not { } idExecucao)
            return new(AcaoDoTrabalho.Descartar, null, "Trabalho sem execução a avançar.");
        var execucao = await execucoes.ObterPorIdAsync(idExecucao, cancellationToken);
        var lista = execucao is null ? [] : await etapas.ListarDaExecucaoAsync(execucao.Id, cancellationToken);
        var etapa = trabalho.IdEtapa is { } idEtapa
            ? lista.SingleOrDefault(e => e.Id == idEtapa)
            : lista.FirstOrDefault(e => !e.Terminal);
        if (execucao is null || etapa is null) return new(AcaoDoTrabalho.Descartar, null, "Execução ou etapa inexistente.");
        var alvo = Alvo(execucao, etapa);
        if (execucao.Terminal) return new(AcaoDoTrabalho.Descartar, alvo, "Execução já encerrada.");
        // Pedido do dono (#305): nem executa nem retoma; quem reivindicou grava Cancelada sob posse.
        if (await PedidoAsync(execucao.Id, cancellationToken) is { } pedido)
            return new(AcaoDoTrabalho.Cancelar, alvo, PedidoDeCancelamento.MotivoDaTransicao(PedidoDeCancelamento.Motivo(pedido.Dados)));
        return etapa.Estado switch
        {
            EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta when execucao.Estado is EstadoDaExecucao.Pendente or
                EstadoDaExecucao.EmExecucao => new(AcaoDoTrabalho.Executar, alvo, null, Origem(trabalho.Dados)),
            EstadoDaEtapa.EmExecucao => new(AcaoDoTrabalho.Interromper, alvo,
                "A tentativa anterior estava em execução quando a posse do trabalho se perdeu (reinício do Worker ou lease vencido)."),
            _ => new(AcaoDoTrabalho.Descartar, alvo, "Nada a avançar no estado atual.")
        };
    }

    public Task IniciarEtapaAsync(PosseDoTrabalho posse, Guid idEtapa, CancellationToken cancellationToken = default) =>
        fila.GravarComPosseAsync(posse, async token =>
        {
            var (execucao, etapa) = await CarregarAsync(idEtapa, token);
            if (etapa.Estado is not (EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta))
                throw new TransicaoInvalidaException("etapa", etapa.Estado.ToString(), EstadoDaEtapa.EmExecucao.ToString());
            // O pedido gravado antes do início impede qualquer processo (#305); a linha do trabalho está travada, então
            // um pedido concorrente ou já está visível aqui ou só é gravado depois do início.
            if (await PedidoAsync(execucao.Id, token) is not null) throw new CancelamentoSolicitadoException();
            await IniciarAsync(execucao, etapa, fila.Agora, token);
            await unidade.SalvarAlteracoesAsync(token);
        }, null, cancellationToken: cancellationToken);

    public Task VincularSessaoAsync(PosseDoTrabalho posse, Guid idEtapa, long numeroDaSessao,
        CancellationToken cancellationToken = default) =>
        fila.GravarComPosseAsync(posse, async token =>
        {
            var (execucao, etapa) = await CarregarAsync(idEtapa, token);
            if (etapa.Estado != EstadoDaEtapa.EmExecucao)
                throw new InvalidOperationException("Só uma etapa em execução recebe a sessão que a executa.");
            var sessao = await sessoes.ObterPorNumeroAsync(numeroDaSessao, token) ??
                throw new InvalidOperationException("Sessão não registrada no ledger.");
            // A sessão da etapa é do dono da execução, no mesmo canal; nunca a de outro usuário.
            if (sessao.Canal != execucao.Canal || sessao.IdDoDono != execucao.IdDoUsuarioNoCanal)
                throw new InvalidOperationException("A sessão não pertence ao dono da execução.");
            etapa.VincularSessao(sessao, null);
            await unidade.SalvarAlteracoesAsync(token);
        }, null, cancellationToken: cancellationToken);

    public async Task<EstadoDaEtapa> ConcluirEtapaAsync(PosseDoTrabalho posse, Guid idEtapa,
        ResultadoDaEtapaDto resultado, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        if (resultado.EstadoFinal is not (EstadoDaEtapa.Concluida or EstadoDaEtapa.Falhou or EstadoDaEtapa.Cancelada))
            throw new ArgumentException("Estado final da etapa inválido.", nameof(resultado));
        var estadoFinal = resultado.EstadoFinal;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            // Travada com os ancestrais (#309): a decisão entre concluir, esperar as delegações ou retomar a origem lê o
            // estado atual delas, e uma subordinada que termina ao mesmo tempo espera esta transação (ou vice-versa).
            var (execucao, etapa) = await CarregarAsync(idEtapa, token, travar: true);
            var agora = fila.Agora;
            // Pedido do dono registrado antes da conclusão (#305): o resultado é descartado e a etapa termina Cancelada.
            string? motivoDoPedido = null;
            if (await PedidoAsync(execucao.Id, token) is { } pedido)
            {
                motivoDoPedido = PedidoDeCancelamento.MotivoDaTransicao(PedidoDeCancelamento.Motivo(pedido.Dados));
                resultado = resultado with { EstadoFinal = EstadoDaEtapa.Cancelada };
            }
            // Falha ou conclusão antes do início (o job legado falha antes de iniciar quando os anexos não estão
            // disponíveis): a tentativa conta como iniciada e termina no mesmo instante, como no ledger da #299.
            if (resultado.EstadoFinal != EstadoDaEtapa.Cancelada && etapa.Estado is EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta)
                await IniciarAsync(execucao, etapa, agora, token);
            var etapaAntes = etapa.Estado;
            var execucaoAntes = execucao.Estado;
            switch (resultado.EstadoFinal)
            {
                case EstadoDaEtapa.Concluida:
                    etapa.Concluir(resultado.CodigoDeSaida, agora);
                    var proxima = (await etapas.ListarDaExecucaoAsync(execucao.Id, token)).FirstOrDefault(e => !e.Terminal);
                    // A etapa delegou (#309): a origem espera as delegações abertas ou é retomada com os desfechos.
                    if (proxima is null && await CicloDaDelegacao.AoConcluirEtapaDaOrigemAsync(Ciclo, execucao, etapa, agora, token))
                        break;
                    if (proxima is null) execucao.Concluir(agora);
                    else await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, proxima,
                        ChaveDeAvanco(execucao, proxima), agora), token);
                    break;
                case EstadoDaEtapa.Falhou:
                    var erro = string.IsNullOrWhiteSpace(resultado.Erro) ? "Falha sem detalhe informado." : resultado.Erro;
                    etapa.Falhar(erro, resultado.CodigoDeSaida, agora);
                    execucao.Falhar(erro, agora);
                    break;
                default:
                    etapa.Cancelar(motivoDoPedido, resultado.CodigoDeSaida, agora);
                    if (!execucao.Terminal) execucao.Cancelar(motivoDoPedido, agora);
                    break;
            }
            await TransicaoAsync(execucao, etapa, etapaAntes, agora, token);
            if (execucao.Estado != execucaoAntes) await TransicaoAsync(execucao, null, execucaoAntes, agora, token);
            await LiberarWorktreesAsync(execucao, agora, token);
            await unidade.SalvarAlteracoesAsync(token);
            estadoFinal = etapa.Estado;
        }, EstadoDoTrabalho.Concluido, cancellationToken: cancellationToken);
        return estadoFinal;
    }

    public async Task<ExecucaoInterrompidaDto> CancelarSolicitadaAsync(PosseDoTrabalho posse, AlvoDoTrabalhoDto alvo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alvo);
        var novoEstado = EstadoDaExecucao.Cancelada;
        var motivo = PedidoDeCancelamento.MotivoDaTransicao(null);
        var transitou = false;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (execucao, etapa) = await CarregarAsync(alvo.IdEtapa, token);
            var pedido = await PedidoAsync(execucao.Id, token) ??
                throw new InvalidOperationException("Execução sem pedido de cancelamento registrado.");
            motivo = PedidoDeCancelamento.MotivoDaTransicao(PedidoDeCancelamento.Motivo(pedido.Dados));
            novoEstado = execucao.Estado;
            if (execucao.Terminal) return;
            var agora = fila.Agora;
            var etapaAntes = etapa.Estado;
            var execucaoAntes = execucao.Estado;
            if (!etapa.Terminal)
            {
                etapa.Cancelar(motivo, null, agora);
                await TransicaoAsync(execucao, etapa, etapaAntes, agora, token);
            }
            execucao.Cancelar(motivo, agora);
            await TransicaoAsync(execucao, null, execucaoAntes, agora, token);
            novoEstado = execucao.Estado;
            await LiberarWorktreesAsync(execucao, agora, token);
            await unidade.SalvarAlteracoesAsync(token);
            transitou = true;
        }, EstadoDoTrabalho.Descartado, ControleDeExecucoesAppService.MotivoDoDescarte, cancellationToken: cancellationToken);
        // PorCancelamento só quando esta gravação fez a transição: execução já terminal não gera aviso.
        return new ExecucaoInterrompidaDto(alvo, novoEstado, motivo, PorCancelamento: transitou);
    }

    public async Task<bool> CancelamentoSolicitadoAsync(Guid idExecucao, CancellationToken cancellationToken = default) =>
        await PedidoAsync(idExecucao, cancellationToken) is not null;

    public async Task<ExecucaoInterrompidaDto> InterromperAsync(PosseDoTrabalho posse, AlvoDoTrabalhoDto alvo,
        string motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alvo);
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        var automatica = alvo.PoliticaDeRetomada == PoliticaDeRetomada.Automatica;
        var novoEstado = EstadoDaExecucao.Interrompida;
        var motivoFinal = motivo;
        var agora = fila.Agora;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (execucao, etapa) = await CarregarAsync(alvo.IdEtapa, token);
            if (etapa.PoliticaDeRetomada != alvo.PoliticaDeRetomada)
                throw new InvalidOperationException("A política da etapa mudou desde a avaliação.");
            var etapaAntes = etapa.Estado;
            var execucaoAntes = execucao.Estado;
            etapa.Interromper(motivo);
            // Ação externa de resultado incerto: bloqueada para o humano conferir; nunca volta à fila sozinha.
            if (etapa.PoliticaDeRetomada == PoliticaDeRetomada.Nunca)
            {
                motivoFinal = MotivoDeResultadoIncerto;
                execucao.Bloquear(motivoFinal);
            }
            else execucao.Interromper(motivo);
            await TransicaoAsync(execucao, etapa, etapaAntes, agora, token);
            await TransicaoAsync(execucao, null, execucaoAntes, agora, token);
            if (automatica)
            {
                // Leitura idempotente: nova tentativa da etapa, e o mesmo trabalho volta à fila disponível já.
                etapa.Retomar();
                execucao.Retomar();
                await TransicaoAsync(execucao, etapa, EstadoDaEtapa.Interrompida, agora, token);
                await TransicaoAsync(execucao, null, EstadoDaExecucao.Interrompida, agora, token);
            }
            novoEstado = execucao.Estado;
            await unidade.SalvarAlteracoesAsync(token);
        }, automatica ? EstadoDoTrabalho.Pendente : EstadoDoTrabalho.Descartado, motivo, agora, cancellationToken);
        return new ExecucaoInterrompidaDto(alvo, novoEstado, motivoFinal);
    }

    public async Task<ExecucaoInterrompidaDto> EncerrarPorTentativasEsgotadasAsync(PosseDoTrabalho posse,
        AlvoDoTrabalhoDto alvo, string motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alvo);
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        var novoEstado = EstadoDaExecucao.Falhou;
        var motivoFinal = motivo;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (execucao, etapa) = await CarregarAsync(alvo.IdEtapa, token);
            if (etapa.PoliticaDeRetomada != alvo.PoliticaDeRetomada)
                throw new InvalidOperationException("A política da etapa mudou desde a avaliação.");
            novoEstado = execucao.Estado;
            // Só uma etapa pronta para rodar ou que estava rodando é encerrada aqui; em qualquer outro estado (espera de
            // humano, já terminal) o ledger fica como está e só o trabalho termina.
            if (execucao.Terminal || etapa.Estado is not (EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta or EstadoDaEtapa.EmExecucao))
                return;
            var agora = fila.Agora;
            var estavaEmExecucao = etapa.Estado == EstadoDaEtapa.EmExecucao;
            // Não iniciou: a tentativa conta como iniciada e falha no mesmo instante, como em ConcluirEtapaAsync.
            if (!estavaEmExecucao) await IniciarAsync(execucao, etapa, agora, token);
            var etapaAntes = etapa.Estado;
            var execucaoIniciada = execucao.Estado;
            if (estavaEmExecucao && etapa.PoliticaDeRetomada == PoliticaDeRetomada.ComConfirmacao)
            {
                etapa.Interromper(motivo);
                execucao.Interromper(motivo);
            }
            else if (estavaEmExecucao && etapa.PoliticaDeRetomada == PoliticaDeRetomada.Nunca)
            {
                // A tentativa anterior pode ter produzido efeito externo: bloqueada, nunca uma falha comum.
                motivoFinal = MotivoDeResultadoIncerto;
                etapa.Interromper(motivo);
                execucao.Bloquear(motivoFinal);
            }
            else
            {
                etapa.Falhar(motivo, null, agora);
                execucao.Falhar(motivo, agora);
            }
            await TransicaoAsync(execucao, etapa, etapaAntes, agora, token);
            if (execucao.Estado != execucaoIniciada) await TransicaoAsync(execucao, null, execucaoIniciada, agora, token);
            novoEstado = execucao.Estado;
            await LiberarWorktreesAsync(execucao, agora, token);
            await unidade.SalvarAlteracoesAsync(token);
        }, EstadoDoTrabalho.Falhou, motivo, cancellationToken: cancellationToken);
        return new ExecucaoInterrompidaDto(alvo, novoEstado, motivoFinal);
    }

    public async Task<ExecucaoInterrompidaDto> CancelarNaoIniciadaAsync(PosseDoTrabalho posse, AlvoDoTrabalhoDto alvo,
        string motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alvo);
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (execucao, etapa) = await CarregarAsync(alvo.IdEtapa, token);
            var agora = fila.Agora;
            var etapaAntes = etapa.Estado;
            var execucaoAntes = execucao.Estado;
            etapa.Cancelar(motivo, null, agora);
            execucao.Cancelar(motivo, agora);
            await TransicaoAsync(execucao, etapa, etapaAntes, agora, token);
            await TransicaoAsync(execucao, null, execucaoAntes, agora, token);
            await LiberarWorktreesAsync(execucao, agora, token);
            await unidade.SalvarAlteracoesAsync(token);
        }, EstadoDoTrabalho.Descartado, motivo, cancellationToken: cancellationToken);
        return new ExecucaoInterrompidaDto(alvo, EstadoDaExecucao.Cancelada, motivo);
    }

    public Task DescartarAsync(PosseDoTrabalho posse, string motivo, CancellationToken cancellationToken = default) =>
        fila.GravarComPosseAsync(posse, null, EstadoDoTrabalho.Descartado, motivo, cancellationToken: cancellationToken);

    public async Task<bool> ConfirmarRetomadaAsync(long numero, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.ValidarNumero(numero);
        var execucao = await execucoes.ObterPorNumeroAsync(numero, cancellationToken);
        if (execucao is not { Estado: EstadoDaExecucao.Interrompida or EstadoDaExecucao.Bloqueada }) return false;
        if (await PedidoAsync(execucao.Id, cancellationToken) is not null) return false;
        var etapa = (await etapas.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
            .FirstOrDefault(e => e.Estado == EstadoDaEtapa.Interrompida);
        if (etapa is null) return false;
        var agora = fila.Agora;
        var execucaoAntes = execucao.Estado;
        etapa.Retomar();
        execucao.Retomar();
        await TransicaoAsync(execucao, etapa, EstadoDaEtapa.Interrompida, agora, cancellationToken);
        await TransicaoAsync(execucao, null, execucaoAntes, agora, cancellationToken);
        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, etapa,
            $"retomar:{execucao.Id:N}:{etapa.Numero}:{etapa.Tentativa}", agora), cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return true;
    }

    private async Task IniciarAsync(Execucao execucao, EtapaDaExecucao etapa, DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (execucao.Estado == EstadoDaExecucao.Pendente)
        {
            execucao.Iniciar(agora);
            await TransicaoAsync(execucao, null, EstadoDaExecucao.Pendente, agora, cancellationToken);
        }
        var etapaAntes = etapa.Estado;
        if (etapa.Estado == EstadoDaEtapa.Pendente)
        {
            var requeridas = (await dependencias.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
                .Where(d => d.IdEtapa == etapa.Id).Select(d => d.IdEtapaRequerida).ToHashSet();
            etapa.TornarPronta((await etapas.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
                .Where(e => requeridas.Contains(e.Id)));
        }
        etapa.Iniciar(agora);
        await TransicaoAsync(execucao, etapa, etapaAntes, agora, cancellationToken);
    }

    // Execução que acabou de ficar terminal libera as próprias worktrees na mesma transação (#307, AD-71) e leva o ciclo
    // das delegações junto (#309): a delegação da subordinada termina e a origem pode ser retomada; uma origem encerrada
    // pede o cancelamento das subordinadas abertas.
    private async Task LiberarWorktreesAsync(Execucao execucao, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await LiberacaoDeWorktrees.LiberarSeEncerradaAsync(reservas, eventos, execucao, agora, cancellationToken);
        await CicloDaDelegacao.AoEncerrarAsync(Ciclo, execucao, agora, cancellationToken);
    }

    private CicloDaDelegacao.Servicos Ciclo => new(delegacoes, execucoes, etapas, eventos, fila);

    // O primeiro pedido de cancelamento registrado para a execução (#305), lido do banco a cada chamada.
    private async Task<EventoDeOrquestracao?> PedidoAsync(Guid idExecucao, CancellationToken cancellationToken) =>
        (await eventos.ConsultarDoTipoAsync([idExecucao], TipoDeEventoDeOrquestracao.CancelamentoSolicitado,
            cancellationToken)).FirstOrDefault();

    private async Task<(Execucao Execucao, EtapaDaExecucao Etapa)> CarregarAsync(Guid idEtapa,
        CancellationToken cancellationToken, bool travar = false)
    {
        var etapa = await etapas.ObterPorIdAsync(idEtapa, cancellationToken) ??
            throw new InvalidOperationException("Etapa não registrada no ledger.");
        var execucao = (travar
            ? await execucoes.TravarComAncestraisAsync(etapa.IdExecucao, cancellationToken)
            : await execucoes.ObterPorIdAsync(etapa.IdExecucao, cancellationToken)) ??
            throw new InvalidOperationException("Execução não registrada no ledger.");
        return (execucao, etapa);
    }

    // Etapa nula: transição da própria execução. Mesmo formato de evento do ledger (AD-66).
    private Task TransicaoAsync(Execucao execucao, EtapaDaExecucao? etapa, Enum de, DateTimeOffset quando,
        CancellationToken cancellationToken) =>
        eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao,
            etapa is null ? TipoDeEventoDeOrquestracao.ExecucaoTransitou : TipoDeEventoDeOrquestracao.EtapaTransitou,
            JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["de"] = de.ToString(),
                ["para"] = (etapa is null ? (Enum)execucao.Estado : etapa.Estado).ToString()
            }), quando, etapa), cancellationToken);

    private static AlvoDoTrabalhoDto Alvo(Execucao execucao, EtapaDaExecucao etapa) => new(execucao.Id, execucao.Numero,
        execucao.Codigo, execucao.Tipo, execucao.Estado, execucao.Canal, execucao.IdDoUsuarioNoCanal, etapa.Id,
        etapa.Numero, etapa.Estado, etapa.Escrita, etapa.PoliticaDeRetomada, etapa.Tentativa);

    private static string? Origem(string? dados)
    {
        if (dados is null) return null;
        try
        {
            using var json = JsonDocument.Parse(dados);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("origem", out var origem) && origem.ValueKind == JsonValueKind.String
                ? origem.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ChaveDeAvanco(Execucao execucao, EtapaDaExecucao etapa) =>
        $"avancar:{execucao.Id:N}:{etapa.Numero}:{etapa.Tentativa}";

    private static void ValidarChave(string chave)
    {
        if (string.IsNullOrWhiteSpace(chave) || chave.Length > TrabalhoDaFila.TamanhoMaximoDaChave)
            throw new ArgumentException("Chave de deduplicação inválida.", nameof(chave));
    }
}
