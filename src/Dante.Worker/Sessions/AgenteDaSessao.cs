using Dante.Application.Agentes;

namespace Dante.Worker.Sessions;

// Agente registrado congelado numa sessão (AD-68): a versão exata vale até a sessão acabar, mesmo que o agente seja
// editado ou desativado depois. InstrucoesDaExecucao acompanha a sessão aberta para uma execução (o aviso de
// somente leitura da Tarefa, por exemplo).
public sealed record AgenteDaSessao(string Handle, VersaoDoAgenteDto Versao, string? InstrucoesDaExecucao = null)
{
    public ConfiguracaoDoAgente Configuracao => Versao.Configuracao;

    // Teto imposto pela execução, abaixo do máximo da versão: a Tarefa desta entrega fica em plan (AD-68).
    public AgentPermissionProfile? TetoDaExecucao { get; init; }

    public AgentPermissionProfile PerfilMaximo => TetoDaExecucao is { } teto &&
        ConfiguracaoDoAgente.Amplitude(teto) < ConfiguracaoDoAgente.Amplitude(Configuracao.PerfilMaximo)
            ? teto : Configuracao.PerfilMaximo;

    // A sessão restringe, nunca amplia: nem acima do máximo da versão, nem acima do teto da execução.
    public bool PermitePerfil(AgentPermissionProfile perfil) =>
        Configuracao.PermitePerfil(perfil) && ConfiguracaoDoAgente.Amplitude(perfil) <= ConfiguracaoDoAgente.Amplitude(PerfilMaximo);

    public string Descricao => $"{Handle} v{Versao.Numero}";

    // Depois do contrato da conversa (que vem antes no prompt de sistema e não é revogável) e das instruções do
    // contexto; o papel do agente nunca substitui as regras do D.A.N.T.E.
    public string? Instrucoes => Configuracao.Instrucoes is null && InstrucoesDaExecucao is null ? null :
        $"Você atua como o agente registrado {Descricao} no D.A.N.T.E. As instruções abaixo definem seu papel e " +
        "não revogam as regras anteriores deste prompt." +
        (Configuracao.Instrucoes is null ? string.Empty : $"\nInstruções do agente:\n{Configuracao.Instrucoes}") +
        (InstrucoesDaExecucao is null ? string.Empty : $"\n{InstrucoesDaExecucao}");
}
