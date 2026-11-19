using Dante.Application.Agentes;

namespace Dante.Worker.Sessions;

// Agente registrado congelado numa sessão (AD-68): a versão exata vale até a sessão acabar, mesmo que o agente seja
// editado ou desativado depois. InstrucoesDaExecucao acompanha a sessão aberta para uma execução (o aviso de
// somente leitura da Tarefa, por exemplo).
public sealed record AgenteDaSessao(string Handle, VersaoDoAgenteDto Versao, string? InstrucoesDaExecucao = null)
{
    public ConfiguracaoDoAgente Configuracao => Versao.Configuracao;

    // Teto imposto pela execução, abaixo do máximo da versão: a Tarefa de leitura fica em plan (AD-68).
    public AgentPermissionProfile? TetoDaExecucao { get; init; }

    // Etapa de escrita de execução nova (#307): a sessão só abre com confinamento de filesystem confirmado pelo driver
    // (só as worktrees graváveis, diretório Git comum somente leitura, sem aprovação interativa nem ferramentas do
    // D.A.N.T.E.). Driver sem esse confinamento recusa antes de iniciar processo.
    public bool EscritaConfinada { get; init; }

    // Checkouts principais dos repositórios da escrita confinada: só para o driver declará-los não confiáveis à CLI (o
    // Codex grava em ~/.codex/config.toml a confiança do repositório de uma sessão nova). Nunca diretório de trabalho,
    // raiz gravável nem instrução ao agente.
    public IReadOnlyList<string> CheckoutsDaEscrita { get; init; } = [];

    // Revisão de um Roteiro (#308, decisão D): sessão reservada em plan, com a worktree da execução como diretório de
    // trabalho e sandbox de SO somente leitura confirmado pelo driver (sem rede nem aprovação interativa). O único
    // servidor de ferramentas é o dante_coordenacao, no escopo abaixo, fixado pelo orquestrador.
    public bool LeituraConfinada { get; init; }

    public Dante.Application.Execucoes.EscopoDaRevisaoDto? EscopoDaRevisao { get; init; }

    // Delegação (#309, PR 3): a sessão da etapa de origem (delega) ou da subordinada (entrega o resultado) recebe o
    // dante_coordenacao no perfil deste escopo, fixado pelo orquestrador; o agente nunca informa execução, etapa nem dono.
    public Dante.Worker.Coordenacao.EscopoDeCoordenacao? Coordenacao { get; init; }

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
