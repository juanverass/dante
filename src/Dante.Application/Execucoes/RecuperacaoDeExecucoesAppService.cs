using System.Text.Json;
using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public sealed class RecuperacaoDeExecucoesAppService(
    IFilaDeTrabalho fila,
    ISessaoDeAgenteRepository sessoes,
    IEventoDeOrquestracaoRepository eventos,
    IUnitOfWorkDaOrquestracao unidade,
    IUnidadesDeContencao unidades) : IRecuperacaoDeExecucoesAppService
{
    public const string MotivoDaSessaoInterrompida =
        "O Worker reiniciou: a sessão e o processo do agente não sobrevivem ao reinício.";

    public async Task<RelatorioDeRecuperacaoDto> RecuperarAsync(IdentidadeDoProcessador atual,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(atual);
        var paradas = new List<string>();
        var desconhecidas = new List<string>();
        var falhas = new List<string>();
        var listagemIndisponivel = false;
        IReadOnlyList<string> listadas = [];
        try
        {
            listadas = await unidades.ListarAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sem listagem não se para nada: parar exige comparar com o que o gerenciador informa.
            listagemIndisponivel = true;
        }
        if (listadas.Count > 0)
        {
            // Só para o que o ledger registra como de um boot anterior; o resto é relatado.
            var registradas = (await sessoes.ListarPorUnidadesDeContencaoAsync(listadas.ToArray(), cancellationToken))
                .GroupBy(s => s.UnidadeDeContencao!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.All(s => s.BootDoWorker is { } boot && boot != atual.Boot),
                    StringComparer.Ordinal);
            foreach (var nome in listadas.Distinct(StringComparer.Ordinal))
            {
                if (!registradas.TryGetValue(nome, out var deBootAnterior) || !deBootAnterior)
                {
                    desconhecidas.Add(nome);
                    continue;
                }
                try
                {
                    await unidades.PararAsync(nome, cancellationToken);
                    paradas.Add(nome);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    falhas.Add(nome);
                }
            }
        }

        var interrompidas = 0;
        foreach (var sessao in await sessoes.ListarNaoTerminaisAsync(cancellationToken))
        {
            if (sessao.BootDoWorker == atual.Boot) continue;
            var antes = sessao.Estado;
            var agora = fila.Agora;
            sessao.Interromper(MotivoDaSessaoInterrompida, null, agora);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaSessao(sessao, TipoDeEventoDeOrquestracao.SessaoTransitou,
                null, JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["de"] = antes.ToString(),
                    ["para"] = sessao.Estado.ToString(),
                    ["motivo"] = "recuperacao"
                }), agora), cancellationToken);
            interrompidas++;
        }
        try
        {
            if (interrompidas > 0) await unidade.SalvarAlteracoesAsync(cancellationToken);
        }
        catch (ConflitoDeConcorrenciaException)
        {
            // Outra gravação mexeu numa dessas sessões; a próxima inicialização tenta de novo.
            interrompidas = 0;
        }

        var devolvidos = await fila.DevolverDeBootsAnterioresAsync(atual, cancellationToken);
        return new RelatorioDeRecuperacaoDto(paradas, desconhecidas, falhas, listagemIndisponivel, interrompidas,
            devolvidos);
    }
}
