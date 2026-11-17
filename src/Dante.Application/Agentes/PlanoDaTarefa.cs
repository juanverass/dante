using Dante.Application.Contextos;
using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Execução Tarefa pronta para ser criada e despachada (AD-68): agente e versão congelados, contexto resolvido (AD-27),
// perfil efetivo e etapas. Sem o pedido explícito de escrita (#307), o perfil é sempre plan e nenhuma etapa escreve,
// qualquer que seja a versão. Com ele, uma única etapa de escrita no perfil padrão da versão, executada só em worktrees
// reservadas (AD-71). Pedido vai ao agente; ObjetivoRegistrado é o que o ledger guarda (cortado, e redigido se parecer
// segredo).
public sealed record PlanoDaTarefa(
    string Handle,
    VersaoDoAgenteDto Versao,
    JobExecutionContext Contexto,
    string Pedido,
    string ObjetivoRegistrado,
    AgentPermissionProfile Perfil,
    IReadOnlyList<EtapaPlanejada> Etapas,
    bool Escrita = false)
{
    public bool SomenteLeitura => !Escrita && Perfil == AgentPermissionProfile.Plan && Etapas.All(etapa => !etapa.Escrita);

    // A forma exata que MontagemDaTarefa produz para escrita; qualquer outra combinação é recusada antes de gravar.
    public bool EscritaValida => Escrita && Perfil != AgentPermissionProfile.Plan &&
        Perfil == Versao.Configuracao.PerfilPadrao && Contexto.Mode == JobExecutionMode.Repository &&
        ConfinamentoDeEscrita.Suporta(Versao.Configuracao.Driver) &&
        Etapas is [{ Escrita: true, PoliticaDeRetomada: Dante.Domain.Execucoes.PoliticaDeRetomada.ComConfirmacao }];
}
