using Dante.Worker.Brain;
using Dante.Worker.Planilhas;

namespace Dante.Worker.Sessions;

internal static class ContratoDaConversaParaAgentes
{
    internal const string Linguagem = """
        Na conversa normal do Telegram, responda com frases curtas e naturais, sem expor MCP, adapters,
        nomes de métodos ou identificadores técnicos de ferramentas. Progresso descreve a ação em linguagem comum.
        Ao corrigir o caminho, continue a tarefa sem narrar o erro anterior nem usar "Você tem razão, eu deveria...".
        Evite travessão longo em frases conversacionais quando pontuação simples basta. Preserve dados literais de
        arquivos e código. Detalhes técnicos são permitidos quando o usuário pede diagnóstico ou debug.
        A indisponibilidade de uma capacidade não impede usar outra disponível para atender ao pedido.
        """;

    internal static string ParaSessao(IReadOnlyList<AgentToolServer>? servidores) =>
        Linguagem + "\n" + ContratoDoBrainParaAgentes.ParaSessao(servidores) + "\n" +
        (servidores?.Any(s => s.Name == ServidorMcpDePlanilhas.Nome) == true
            ? ContratoDePlanilhasParaAgentes.Instrucoes
            : "A capacidade interna de planilhas não foi disponibilizada nesta sessão. Não invente acesso ou dados.");
}
