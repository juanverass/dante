using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dante.ProcessProbe;

// Servidor MCP stdio de teste (#308): ferramentas sem anotações, que o Codex trata como destrutivas e de mundo aberto
// e, portanto, sujeitas a aprovação (mcpServer/elicitation/request). Cada chamada aceita é anotada no arquivo de
// registro, para a evidência saber o que de fato executou.
internal static class FakeMcpComAprovacao
{
    public static int Run(string[] args)
    {
        var registro = args.Length > 1 ? args[1] : null;
        while (Console.In.ReadLine() is { } linha)
        {
            var pedido = JsonNode.Parse(linha)!.AsObject();
            if (pedido["id"] is not { } id) continue;
            JsonObject resultado = (string?)pedido["method"] switch
            {
                "initialize" => new()
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "dante_coordenacao", ["version"] = "1" }
                },
                "tools/list" => new()
                {
                    ["tools"] = new JsonArray(Ferramenta("entregar_resultado"), Ferramenta("outra_ferramenta"))
                },
                "tools/call" => Chamada(pedido["params"]!.AsObject(), registro),
                _ => new()
            };
            Console.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = resultado }.ToJsonString());
        }
        return 0;
    }

    private static JsonObject Ferramenta(string nome) => new()
    {
        ["name"] = nome, ["description"] = "teste",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
    };

    private static JsonObject Chamada(JsonObject parametros, string? registro)
    {
        var nome = (string?)parametros["name"];
        if (registro is not null) File.AppendAllText(registro, nome + "\n");
        return new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "executada " + nome }), ["isError"] = false
        };
    }
}
