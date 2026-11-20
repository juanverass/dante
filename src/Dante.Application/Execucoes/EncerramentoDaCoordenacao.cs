using System.Text.Json;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Fim de uma Coordenada (AD-75, #310), por qualquer caminho (conclusão, falha, cancelamento pelo dono, tentativas
// esgotadas, plano recusado), dentro da transação que a tornou terminal: nada fica em aberto num ledger encerrado. A
// revisão que aguardava a decisão (ou a correção pedida no gate) termina Cancelado; as etapas não terminais param, as
// que não começaram como Ignorada e as em curso como Cancelada, com o motivo. Um plano já aprovado continua Aprovado (o
// banco não o reabre), e nada é apagado. As subordinadas abertas recebem o pedido de cancelamento pelo ciclo das
// delegações (#309), chamado por quem encerra.
internal static class EncerramentoDaCoordenacao
{
    public static async Task AoEncerrarAsync(IPlanoDaCoordenacaoRepository planos, IEtapaDaExecucaoRepository etapas,
        IEventoDeOrquestracaoRepository eventos, Execucao execucao, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        if (execucao.Tipo != TipoDeExecucao.Coordenada || !execucao.Terminal) return;
        foreach (var plano in await planos.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
            if (plano.Estado == EstadoDoPlano.AguardandoAprovacao) plano.Cancelar(agora);
        var motivo = $"A coordenação terminou ({execucao.Estado}).";
        foreach (var etapa in (await etapas.ListarDaExecucaoAsync(execucao.Id, cancellationToken)).Where(e => !e.Terminal))
        {
            var antes = etapa.Estado;
            if (etapa.Estado is EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta) etapa.Ignorar(motivo);
            else etapa.Cancelar(motivo, null, agora);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.EtapaTransitou,
                JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["de"] = antes.ToString(),
                    ["para"] = etapa.Estado.ToString()
                }), agora, etapa), cancellationToken);
        }
    }
}
