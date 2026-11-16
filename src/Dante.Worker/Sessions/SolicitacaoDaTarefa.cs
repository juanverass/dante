using Dante.Application.Agentes;

namespace Dante.Worker.Sessions;

// Sessão que executa a etapa de uma Tarefa (AD-68): perfil do plano (sempre plan nesta entrega), modelo e esforço da
// versão congelada e o aviso de somente leitura nas instruções. O SessionRegistry ainda confere perfil, repositórios
// e ferramentas contra a versão.
public static class SolicitacaoDaTarefa
{
    public static SessionStartRequest Para(PlanoDaTarefa plano, long idDoUsuario,
        IReadOnlyDictionary<string, string>? ambiente = null)
    {
        ArgumentNullException.ThrowIfNull(plano);
        if (!plano.SomenteLeitura) throw new Dante.Domain.Execucoes.EscritaSemWorktreeException();
        var configuracao = plano.Versao.Configuracao;
        return new SessionStartRequest(idDoUsuario, configuracao.Driver, plano.Contexto, ambiente, plano.Perfil,
            new AgentModelSelection(configuracao.Modelo, configuracao.Esforco),
            RegisteredAgent: new AgenteDaSessao(plano.Handle, plano.Versao, MontagemDaTarefa.AvisoDeLeitura)
            {
                // Nem /mode session tira a sessão da Tarefa de plan.
                TetoDaExecucao = plano.Perfil
            });
    }
}
