using System.Globalization;
using System.Text.Json;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Ciclo operacional entre a origem e as subordinadas (#309, PR 2), sempre dentro da transação que fez a transição. A
// fonte de verdade é o ledger: nenhuma decisão depende de memória do Worker.
// * A origem que conclui uma etapa com delegações dela ainda abertas espera (AguardandoDelegacoes); com todas já
//   terminais, é retomada na hora.
// * A subordinada que termina, por qualquer caminho (resultado, falha, prazo, cancelamento, tentativas esgotadas), leva
//   a delegação ao estado terminal correspondente e, se a origem espera e não resta delegação aberta daquela etapa,
//   retoma a origem: etapa nova de leitura, origem Pendente e trabalho com chave determinística (retomar duas vezes
//   não cria duas etapas nem dois trabalhos).
// * A origem que termina pede o cancelamento das subordinadas abertas pelo evento durável do #305; quem tiver a posse
//   de cada uma o honra, e a delegação termina Cancelada pelo caminho acima.
// Ordem das travas: trabalho, depois a execução com os ancestrais (TravarComAncestraisAsync), então o pai antes da
// filha. A cascata não trava filhas: só acrescenta eventos.
internal static class CicloDaDelegacao
{
    internal sealed record Servicos(
        IDelegacaoRepository Delegacoes,
        IExecucaoRepository Execucoes,
        IEtapaDaExecucaoRepository Etapas,
        IEventoDeOrquestracaoRepository Eventos,
        IFilaDeTrabalho Fila);

    public const string MotivoSemResultado = "A execução subordinada terminou sem resultado entregue pelo contrato.";

    // Chave do trabalho que retoma a origem depois das delegações de uma etapa: uma por etapa de origem.
    public static string ChaveDaRetomada(Execucao origem, EtapaDaExecucao etapaDeOrigem) =>
        $"retomar-origem:{origem.Id:N}:{etapaDeOrigem.Numero.ToString(CultureInfo.InvariantCulture)}";

    // Origem com a etapa recém-concluída e travada. Falso: a etapa não delegou nada e quem chama segue a conclusão comum.
    // Quem chama registra a transição da origem (como faz para qualquer outra conclusão).
    public static async Task<bool> AoConcluirEtapaDaOrigemAsync(Servicos s, Execucao origem, EtapaDaExecucao etapa,
        DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var daEtapa = (await s.Delegacoes.ListarDaOrigemAsync(origem.Id, cancellationToken))
            .Where(d => d.IdEtapaOrigem == etapa.Id).ToList();
        if (daEtapa.Count == 0) return false;
        if (daEtapa.Any(d => !d.Terminal))
        {
            origem.AguardarDelegacoes();
            return true;
        }
        await RetomarAsync(s, origem, etapa, daEtapa.Count, agora, cancellationToken, registrarTransicao: false);
        return true;
    }

    // Execução que acabou de ficar terminal, ainda não gravada. Subordinada: a delegação acompanha e a origem pode ser
    // retomada. Origem: as subordinadas abertas recebem o pedido de cancelamento.
    public static async Task AoEncerrarAsync(Servicos s, Execucao execucao, DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (!execucao.Terminal) return;
        if (execucao.Tipo == TipoDeExecucao.Delegada &&
            await s.Delegacoes.ObterDaSubordinadaAsync(execucao.Id, cancellationToken) is { } delegacao)
        {
            // O pai travado antes de qualquer leitura dele: a decisão de retomar usa o estado atual, não um lido antes.
            await s.Execucoes.TravarComAncestraisAsync(delegacao.IdExecucaoOrigem, cancellationToken);
            if (!delegacao.Terminal)
            {
                var antes = delegacao.Estado;
                switch (execucao.Estado)
                {
                    case EstadoDaExecucao.Cancelada:
                        delegacao.Cancelar(execucao.Motivo ?? "Execução subordinada cancelada.", agora);
                        break;
                    case EstadoDaExecucao.Falhou:
                        delegacao.Falhar(execucao.Erro ?? "Execução subordinada falhou.", agora);
                        break;
                    default:
                        delegacao.Falhar(MotivoSemResultado, agora);
                        break;
                }
                await TransicaoDaDelegacaoAsync(s, delegacao, antes, agora, cancellationToken);
            }
            await RetomarSeProntaAsync(s, delegacao, agora, cancellationToken);
        }
        await CancelarSubordinadasAbertasAsync(s, execucao, agora, cancellationToken);
    }

    // A origem espera e nenhuma delegação daquela etapa continua aberta: retoma. Idempotente pela origem travada (só sai
    // de AguardandoDelegacoes uma vez) e pela chave do trabalho.
    public static async Task<bool> RetomarSeProntaAsync(Servicos s, Delegacao delegacao, DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        var origem = await s.Execucoes.TravarComAncestraisAsync(delegacao.IdExecucaoOrigem, cancellationToken) ??
            throw new InvalidOperationException("Execução de origem não registrada no ledger.");
        if (origem.Estado != EstadoDaExecucao.AguardandoDelegacoes) return false;
        // A delegação desta transação vem rastreada, com o estado novo; as demais, como gravadas.
        var daEtapa = (await s.Delegacoes.ListarDaOrigemAsync(origem.Id, cancellationToken))
            .Where(d => d.IdEtapaOrigem == delegacao.IdEtapaOrigem).ToList();
        if (daEtapa.Any(d => !d.Terminal)) return false;
        var etapa = await s.Etapas.ObterPorIdAsync(delegacao.IdEtapaOrigem, cancellationToken) ??
            throw new InvalidOperationException("Etapa de origem não registrada no ledger.");
        await RetomarAsync(s, origem, etapa, daEtapa.Count, agora, cancellationToken);
        return true;
    }

    // Mesmo formato dos eventos de transição do ledger, com a identidade da delegação; sempre na execução de origem.
    public static async Task TransicaoDaDelegacaoAsync(Servicos s, Delegacao delegacao, EstadoDaDelegacao de,
        DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var origem = await s.Execucoes.ObterPorIdAsync(delegacao.IdExecucaoOrigem, cancellationToken) ??
            throw new InvalidOperationException("Execução de origem não registrada no ledger.");
        await s.Eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(origem, TipoDeEventoDeOrquestracao.DelegacaoTransitou,
            Dados(delegacao, new() { ["de"] = de.ToString(), ["para"] = delegacao.Estado.ToString() }), agora),
            cancellationToken);
    }

    public static string Dados(Delegacao delegacao, Dictionary<string, string?>? extras = null)
    {
        var dados = new Dictionary<string, string?>
        {
            ["delegacao"] = delegacao.Id.ToString("N", CultureInfo.InvariantCulture),
            ["subordinada"] = delegacao.IdExecucaoSubordinada.ToString("N", CultureInfo.InvariantCulture),
            ["destinatario"] = delegacao.HandleDoDestinatario,
            ["contrato"] = delegacao.Contrato
        };
        foreach (var (chave, valor) in extras ?? []) dados[chave] = valor;
        return JsonSerializer.Serialize(dados);
    }

    private static async Task RetomarAsync(Servicos s, Execucao origem, EtapaDaExecucao etapaDeOrigem, int delegacoes,
        DateTimeOffset agora, CancellationToken cancellationToken, bool registrarTransicao = true)
    {
        var numero = (await s.Etapas.ListarDaExecucaoAsync(origem.Id, cancellationToken)).Max(e => e.Numero) + 1;
        var antes = origem.Estado;
        // Esperando: sai da espera. Ainda em execução (todas já terminais quando a etapa concluiu): volta à fila.
        if (antes == EstadoDaExecucao.AguardandoDelegacoes) origem.Retomar();
        else origem.Reprogramar();
        // Leitura, como a etapa que delegou: a retomada entrega dados ao agente da origem, nunca escreve.
        var retomada = new EtapaDaExecucao(origem, numero, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica,
            agora, etapaDeOrigem.Agente, etapaDeOrigem.Modelo, etapaDeOrigem.Esforco);
        await s.Etapas.AdicionarAsync(retomada, cancellationToken);
        if (registrarTransicao) await TransicaoAsync(s, origem, antes, agora, cancellationToken);
        await s.Eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(origem, TipoDeEventoDeOrquestracao.OrigemRetomada,
            JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["etapaDeOrigem"] = etapaDeOrigem.Numero.ToString(CultureInfo.InvariantCulture),
                ["etapa"] = numero.ToString(CultureInfo.InvariantCulture),
                ["delegacoes"] = delegacoes.ToString(CultureInfo.InvariantCulture)
            }), agora, retomada), cancellationToken);
        await s.Fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(origem, retomada, ChaveDaRetomada(origem, etapaDeOrigem),
            agora), cancellationToken);
    }

    // Pedido de cancelamento (#305) em cada subordinada aberta: só acrescenta eventos, sem travar a filha. O autor é o
    // proprietário da raiz, que é a autoridade de toda a árvore.
    private static async Task CancelarSubordinadasAbertasAsync(Servicos s, Execucao origem, DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        var abertas = (await s.Delegacoes.ListarDaOrigemAsync(origem.Id, cancellationToken)).Where(d => !d.Terminal).ToList();
        if (abertas.Count == 0) return;
        var pedidas = (await s.Eventos.ConsultarDoTipoAsync(abertas.Select(d => d.IdExecucaoSubordinada).ToArray(),
            TipoDeEventoDeOrquestracao.CancelamentoSolicitado, cancellationToken)).Select(e => e.IdExecucao).ToHashSet();
        var autor = new ProprietarioDaExecucao(origem.Canal, origem.IdDoUsuarioNoCanal);
        var motivo = PedidoDeCancelamento.MotivoSeguro($"a execução de origem {origem.Codigo} terminou ({origem.Estado})");
        foreach (var delegacao in abertas.Where(d => !pedidas.Contains(d.IdExecucaoSubordinada)))
        {
            var subordinada = await s.Execucoes.ObterPorIdAsync(delegacao.IdExecucaoSubordinada, cancellationToken) ??
                throw new InvalidOperationException("Execução subordinada não registrada no ledger.");
            await s.Eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(subordinada,
                TipoDeEventoDeOrquestracao.CancelamentoSolicitado, PedidoDeCancelamento.Dados(autor, motivo), agora),
                cancellationToken);
        }
    }

    private static Task TransicaoAsync(Servicos s, Execucao execucao, EstadoDaExecucao de, DateTimeOffset agora,
        CancellationToken cancellationToken) =>
        s.Eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.ExecucaoTransitou,
            JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["de"] = de.ToString(),
                ["para"] = execucao.Estado.ToString()
            }), agora), cancellationToken);
}
