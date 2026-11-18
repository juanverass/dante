using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Worker.Brain;

namespace Dante.Worker.Coordenacao;

// dante_coordenacao (#308, decisão E; AD-70 para a #309): servidor MCP stdio iniciado pela CLI do agente, sem DI,
// configuração, banco ou rede. Só valida a forma das chamadas e as encaminha ao Worker vivo pelo pipe exclusivo da
// sessão, autenticando-se com o token da sessão na primeira linha. Quem decide o que a chamada alcança é o Worker
// (escopo fixado pelo orquestrador ao abrir a sessão), nunca este processo nem o agente. A #309 amplia o catálogo
// aqui e o atendimento no Worker; não cria outro servidor nem outro IPC.
public static class ServidorMcpDeCoordenacao
{
    public const string Nome = "dante_coordenacao";
    public const string Argumento = "--mcp-coordenacao";
    public const string PrefixoDoPipe = "dante-coord-";
    public const string VariavelDoToken = "DANTE_COORDENACAO_TOKEN";
    public const int MaximoEntrada = 16_000;
    public const int MaximoResposta = 32_000;

    public const string EstadoDaExecucao = "estado_da_execucao";
    public const string EntregarResultado = "entregar_resultado";

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record Ferramenta(string Nome, string Descricao, bool Leitura, JsonObject Propriedades, string[] Obrigatorios);

    public static IReadOnlyList<Ferramenta> Ferramentas { get; } =
    [
        new(EstadoDaExecucao,
            "Lê os fatos da revisão em curso desta execução: código, grupo, rodada, limite de rodadas, hash do diff " +
            "revisado, vereditos das rodadas anteriores e a instrução humana da correção, se houver. Somente leitura.",
            true, new JsonObject(), []),
        new(EntregarResultado,
            "Registra o veredito estruturado desta revisão, uma vez só: aprovado (pode seguir para a aprovação humana), " +
            "requer_correcoes (com os itens a corrigir) ou bloqueado (não é possível concluir). O veredito vale para o " +
            "diff que você recebeu; não escreve arquivos nem executa ações externas. Nunca inclua credenciais.",
            false, new JsonObject
            {
                ["veredito"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("aprovado", "requer_correcoes", "bloqueado")
                },
                ["resumo"] = new JsonObject
                {
                    ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 2000,
                    ["description"] = "Avaliação objetiva do diff, em português."
                },
                ["itens"] = new JsonObject
                {
                    ["type"] = "array", ["maxItems"] = 20,
                    ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 500 },
                    ["description"] = "Cada correção necessária, uma por item; obrigatório com requer_correcoes."
                }
            }, ["veredito", "resumo"])
    ];

    public static IReadOnlyList<string> FerramentasDeLeitura => Ferramentas.Where(f => f.Leitura).Select(f => f.Nome).ToArray();

    public static JsonObject Lista() => new()
    {
        ["tools"] = new JsonArray(Ferramentas.Select(f => (JsonNode)new JsonObject
        {
            ["name"] = f.Nome, ["description"] = f.Descricao,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = f.Propriedades.DeepClone(),
                ["required"] = new JsonArray(f.Obrigatorios.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["additionalProperties"] = false
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = f.Leitura, ["destructiveHint"] = false, ["openWorldHint"] = false }
        }).ToArray())
    };

    // Forma da chamada: ferramenta conhecida, objeto, só argumentos declarados e os obrigatórios presentes.
    public static void Validar(string nome, JsonElement argumentos)
    {
        var ferramenta = Ferramentas.SingleOrDefault(f => f.Nome == nome) ?? throw new ArgumentException("Ferramenta desconhecida.");
        if (argumentos.ValueKind != JsonValueKind.Object || argumentos.GetRawText().Length > MaximoEntrada)
            throw new ArgumentException("Argumentos inválidos.");
        foreach (var propriedade in argumentos.EnumerateObject())
            if (!ferramenta.Propriedades.ContainsKey(propriedade.Name)) throw new ArgumentException("Argumento não permitido.");
        foreach (var obrigatorio in ferramenta.Obrigatorios)
            if (!argumentos.TryGetProperty(obrigatorio, out _)) throw new ArgumentException("Argumento obrigatório ausente.");
    }

    public static JsonObject Resultado(object? resultado, bool erro = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(resultado, JsonOptions) }),
        ["isError"] = erro
    };

    // Erro com mensagem curta para o agente; nunca detalhe interno.
    public static JsonObject Erro(string mensagem) => Resultado(new { erro = mensagem }, erro: true);

    public static async Task<int> ExecutarProcessoAsync(string[] args)
    {
        if (args.Length != 3 || args[1] != "--pipe" || !args[2].StartsWith(PrefixoDoPipe, StringComparison.Ordinal)) return 1;
        var token = Environment.GetEnvironmentVariable(VariavelDoToken);
        if (string.IsNullOrWhiteSpace(token)) return 1;
        using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await pipe.ConnectAsync(prazo.Token); } catch { return 1; }
        using var leitor = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var escritor = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await escritor.WriteLineAsync(JsonSerializer.Serialize(new { token }));
        await ExecutarAsync(Console.In, Console.Out, async (nome, argumentos, ct) =>
        {
            await escritor.WriteLineAsync(JsonSerializer.Serialize(new { nome, argumentos }).AsMemory(), ct);
            var resposta = await ServidorMcpDoBrain.LerLinhaAsync(leitor, MaximoResposta, ct) ?? throw new IOException();
            return JsonNode.Parse(resposta)!.AsObject();
        });
        return 0;
    }

    // Laço JSON-RPC do MCP sobre stdio: initialize, ping, tools/list e tools/call; o resto é recusado.
    public static async Task ExecutarAsync(TextReader entrada, TextWriter saida,
        Func<string, JsonElement, CancellationToken, Task<JsonObject>> chamar, CancellationToken ct = default)
    {
        while (await ServidorMcpDoBrain.LerOuDescartarLinhaAsync(entrada, MaximoEntrada, ct) is { } lida)
        {
            JsonObject resposta;
            JsonNode? id = null;
            try
            {
                if (lida.Excedida) throw new ArgumentException("Entrada acima do limite.");
                using var documento = JsonDocument.Parse(lida.Texto!);
                var pedido = documento.RootElement;
                if (!pedido.TryGetProperty("id", out var identificador)) continue;
                id = JsonNode.Parse(identificador.GetRawText());
                var metodo = pedido.GetProperty("method").GetString();
                JsonObject resultado;
                if (metodo == "initialize")
                    resultado = new()
                    {
                        ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = Nome, ["version"] = "1.0.0" },
                        ["instructions"] = "Ferramentas de coordenação do D.A.N.T.E. para esta etapa. Entregue o resultado " +
                            "pela ferramenta entregar_resultado; texto livre não registra veredito."
                    };
                else if (metodo == "ping") resultado = new();
                else if (metodo == "tools/list") resultado = Lista();
                else if (metodo == "tools/call")
                {
                    var parametros = pedido.GetProperty("params");
                    var nome = parametros.GetProperty("name").GetString()!;
                    var argumentos = parametros.TryGetProperty("arguments", out var a) ? a : JsonSerializer.SerializeToElement(new { });
                    Validar(nome, argumentos);
                    resultado = await chamar(nome, argumentos, ct);
                }
                else throw new ArgumentException("Método desconhecido.");
                resposta = new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = resultado };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Forma inválida: mensagem estável; Worker inacessível: indisponível. Nada interno vaza.
                var mensagem = exception is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException
                    ? "Chamada inválida para o dante_coordenacao." : "dante_coordenacao indisponível.";
                resposta = new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Erro(mensagem) };
            }
            var json = resposta.ToJsonString();
            if (json.Length > MaximoResposta)
                json = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = Erro("Resposta acima do limite.") }
                    .ToJsonString();
            await saida.WriteLineAsync(json.AsMemory(), ct);
            await saida.FlushAsync(ct);
            if (lida.Abortada) return;
        }
    }
}
