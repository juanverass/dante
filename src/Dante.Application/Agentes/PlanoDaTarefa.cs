using Dante.Application.Contextos;
using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Execução Tarefa pronta para ser criada e despachada (AD-68): agente e versão congelados, contexto resolvido (AD-27),
// perfil efetivo e etapas. Nesta entrega o perfil é sempre plan e nenhuma etapa escreve, qualquer que seja a versão.
// Pedido vai ao agente; ObjetivoRegistrado é o que o ledger guarda (cortado, e redigido se parecer segredo).
public sealed record PlanoDaTarefa(
    string Handle,
    VersaoDoAgenteDto Versao,
    JobExecutionContext Contexto,
    string Pedido,
    string ObjetivoRegistrado,
    AgentPermissionProfile Perfil,
    IReadOnlyList<EtapaPlanejada> Etapas)
{
    public bool SomenteLeitura => Perfil == AgentPermissionProfile.Plan && Etapas.All(etapa => !etapa.Escrita);
}
