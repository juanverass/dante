using System.Text.Json.Nodes;
using Dante.Application.Planilhas;
using Dante.Worker.Planilhas;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #224: protocolo MCP (stdio, JSON-RPC por linha) do servidor de planilhas que as CLIs iniciam nas sessões.
public sealed class ServidorMcpDePlanilhasTests
{
    [Fact]
    public async Task ProtocoloInicializaListaFerramentasAnotadasEIgnoraNotificacoes()
    {
        using var ambiente = new AmbienteDePlanilhas();
        var servidor = Servidor(ambiente);
        var entrada = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "não é json",
            """{"jsonrpc":"2.0","id":"dois","method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"resources/list"}""",
            """{"jsonrpc":"2.0","id":4,"method":"ping"}""");
        using var saida = new StringWriter();
        await servidor.ExecutarAsync(new StringReader(entrada), saida, CancellationToken.None);
        var respostas = saida.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToArray();

        Assert.Equal(5, respostas.Length);
        Assert.Equal(("2025-03-26", "dante_planilhas"), ((string?)respostas[0]["result"]!["protocolVersion"],
            (string?)respostas[0]["result"]!["serverInfo"]!["name"]));
        Assert.Contains("nunca escolha em silêncio", (string?)respostas[0]["result"]!["instructions"]);
        Assert.Equal(-32700, (int)respostas[1]["error"]!["code"]!);
        var ferramentas = respostas[2]["result"]!["tools"]!.AsArray().Select(t => t!).ToArray();
        Assert.Equal("dois", (string?)respostas[2]["id"]);
        Assert.Equal(ServidorMcpDePlanilhas.FerramentasDeLeitura.Order(),
            ferramentas.Where(t => (bool)t["annotations"]!["readOnlyHint"]!).Select(t => (string)t["name"]!).Order());
        Assert.Equal(["adicionar_linha", "anotar_regiao", "atualizar_celulas", "atualizar_por_referencia", "cadastrar_planilha"],
            ferramentas.Where(t => !(bool)t["annotations"]!["readOnlyHint"]!).Select(t => (string)t["name"]!).Order());
        Assert.Equal(["atualizar_celulas", "atualizar_por_referencia"],
            ferramentas.Where(t => (bool)t["annotations"]!["destructiveHint"]!).Select(t => (string)t["name"]!).Order());
        Assert.All(ferramentas, t => Assert.Equal("object", (string?)t["inputSchema"]!["type"]));
        Assert.Equal(-32601, (int)respostas[3]["error"]!["code"]!);
        Assert.NotNull(respostas[4]["result"]);
    }

    [Fact]
    public async Task FalhasViramResultadoDeErroSemDerrubarOServidor()
    {
        using var ambiente = new AmbienteDePlanilhas(conectado: false);
        var servidor = Servidor(ambiente);
        var (texto, erro) = await Chamar(servidor, "ler_intervalo", new JsonObject { ["planilha"] = "nada", ["intervalo"] = "A1" });
        Assert.True(erro);
        Assert.Contains("não está cadastrada", texto);
        (texto, erro) = await Chamar(servidor, "ler_intervalo", new JsonObject());
        Assert.Equal((true, "Pedido inválido: Informe planilha."), (erro, texto));
        (texto, erro) = await Chamar(servidor, "listar_planilhas", new JsonObject());
        Assert.False(erro);
        Assert.Contains("nenhuma conta conectada", texto);
        (texto, erro) = await Chamar(servidor, "apagar_tudo", new JsonObject());
        Assert.True(erro);
    }

    internal static ServidorMcpDePlanilhas Servidor(AmbienteDePlanilhas ambiente, string agente = "claude") =>
        new(ambiente.Provider.GetRequiredService<IServiceScopeFactory>(), new OrigemDaSolicitacao("mcp", "telegram:42", agente));

    internal static async Task<(string Texto, bool Erro)> Chamar(ServidorMcpDePlanilhas servidor, string ferramenta, JsonObject argumentos)
    {
        var resposta = await servidor.ResponderAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = ferramenta, ["arguments"] = argumentos }
        }, CancellationToken.None);
        var resultado = resposta!["result"]!;
        return ((string)resultado["content"]![0]!["text"]!, (bool)resultado["isError"]!);
    }
}
