namespace Dante.Worker.Sessions;

// Resolve identidade estruturada no limite dos drivers, nunca a descrição livre/argumentos da ferramenta.
// Novas capacidades estendem este catálogo; o transporte não conhece servidores nem operações.
internal static class ApresentacaoDeFerramentas
{
    internal static string Para(string? servidor, string? ferramenta) => servidor switch
    {
        "dante_planilhas" => ferramenta switch
        {
            "listar_planilhas" => "Buscando suas planilhas...",
            "descrever_planilha" => "Analisando a planilha...",
            "ler_intervalo" => "Lendo as informações necessárias...",
            "buscar_na_planilha" => "Procurando na planilha...",
            "atualizar_celulas" or "atualizar_por_referencia" => "Atualizando a planilha...",
            "adicionar_linha" => "Adicionando as informações...",
            "cadastrar_planilha" => "Cadastrando a planilha...",
            "anotar_regiao" => "Anotando a região da planilha...",
            _ => "Consultando suas planilhas..."
        },
        "dante_brain" => "Consultando o Brain...",
        "github" => "Buscando informações no GitHub...",
        _ => ferramenta switch
        {
            "Read" => "Lendo os arquivos...",
            "Glob" or "Grep" => "Procurando nos arquivos...",
            "WebSearch" or "webSearch" => "Buscando informações na internet...",
            "WebFetch" => "Lendo a página...",
            _ => "Executando a tarefa..."
        }
    };

    internal static ToolStartedEvent DoClaude(string id, AgentToolKind tipo, string nome, string descricao)
    {
        string? servidor = null;
        var ferramenta = nome;
        if (nome.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var partes = nome[5..].Split("__", 2, StringSplitOptions.None);
            if (partes.Length == 2) { servidor = partes[0]; ferramenta = partes[1]; }
        }
        return new(id, tipo, descricao)
        {
            Server = servidor, ToolName = ferramenta,
            Presentation = tipo == AgentToolKind.Tool ? Para(servidor, ferramenta) : null
        };
    }

    internal static ToolStartedEvent DoCodex(string id, string? servidor, string? ferramenta, string descricao) =>
        new(id, AgentToolKind.Tool, descricao)
        {
            Server = servidor, ToolName = ferramenta, Presentation = Para(servidor, ferramenta)
        };
}
