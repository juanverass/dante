using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace Dante.Worker.Brain;

// Processo stdio sem DI/configuração/banco. Encaminha somente tools/call ao Worker vivo pelo pipe de uma sessão.
public static class ServidorMcpDoBrain
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() } };
    public const string Nome = "dante_brain";
    public const string Argumento = "--mcp-brain";
    public const int MaximoEntrada = 32_000;
    public const int MaximoResposta = 60_000;
    public sealed record Ferramenta(string Nome, string Descricao, bool Leitura, bool Destrutiva, JsonObject Propriedades, string[] Obrigatorios);
    private static JsonObject Campo(string tipo, string? descricao = null) => new() { ["type"] = tipo, ["description"] = descricao ?? "" };
    private static JsonObject EnumCampo<T>() where T : struct, Enum => new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames<T>().Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
    private static JsonObject Id() => Campo("string", "Identificador retornado por outra ferramenta; nunca solicite GUID ao usuário.");
    private static JsonObject Revisao() => Campo("integer", "Revisão esperada retornada na consulta.");
    private static JsonObject Texto(int maximo) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximo };
    private static JsonObject Tags() => new() { ["type"] = "array", ["maxItems"] = 50, ["items"] = Texto(100) };
    private static JsonObject Captura(bool correcao = false)
    {
        var p = new JsonObject { ["titulo"] = Texto(300), ["conteudo"] = Texto(10_000), ["tags"] = Tags(),
            ["tipo"] = EnumCampo<Dante.Domain.Conhecimentos.TipoDeConhecimento>(),
            ["natureza"] = EnumCampo<Dante.Domain.CapturaDeConhecimento.NaturezaDoConteudo>(),
            ["sensibilidade"] = EnumCampo<Dante.Domain.Conhecimentos.Sensibilidade>(), ["justificativa"] = Texto(2000) };
        if (correcao) { p["id"] = Id(); p["revisao"] = Revisao(); }
        return p;
    }
    public static readonly IReadOnlyList<Ferramenta> Ferramentas =
    [
        new("brain_obter_escopo", "Espaço/projeto autorizado da sessão. Não altera escopo.", true, false, new(), []),
        new("brain_buscar_conhecimento", "Busca permitida; consulte equivalentes antes de capturar e reporte possíveis duplicidades.", true, false,
            new() { ["texto"] = Texto(2000), ["tipo"] = EnumCampo<Dante.Domain.Conhecimentos.TipoDeConhecimento>(), ["tags"] = Tags(), ["limite"] = Revisao(), ["deslocamento"] = Revisao() }, ["texto"]),
        new("brain_mostrar_origem", "Proveniência de candidato ou conhecimento permitido.", true, false, new() { ["id"] = Id(), ["origem"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("candidato", "conhecimento") } }, ["id", "origem"]),
        new("brain_capturar_conhecimento", "Cria um único candidato pendente. Preserve título/conteúdo/tags. Conclusão do agente continua inferência; nunca declare conteúdo do agente como dito pelo usuário.", false, false, Captura(), ["conteudo", "natureza", "justificativa"]),
        new("brain_listar_candidatos", "Candidatos pendentes do escopo, com revisão. Paginação por deslocamento, limite máximo 25.", true, false, new() { ["limite"] = Revisao(), ["deslocamento"] = Revisao() }, []),
        new("brain_corrigir_candidato", "Corrige candidato pendente com revisão esperada, sem redefinir natureza ou escopo.", false, true, Captura(true), ["id", "revisao", "conteudo", "natureza", "justificativa"]),
        new("brain_confirmar_candidato", "PREPARA consolidação. Não consolida pela aprovação MCP. Mostre o candidato e peça ao usuário confirmar no Telegram; uma proposta de cada vez, válida por 5 minutos.", false, false, new() { ["id"] = Id(), ["revisao"] = Revisao() }, ["id", "revisao"]),
        new("brain_cancelar_candidato", "PREPARA descarte auditado; exige confirmação de negócio pelo Telegram.", false, true, new() { ["id"] = Id(), ["revisao"] = Revisao() }, ["id", "revisao"]),
        new("brain_criar_relacao", "PREPARA relação explícita entre dois conhecimentos autorizados com revisões. Peça confirmar no Telegram; sugestões não são relações persistidas.", false, false,
            new() { ["origem"] = Id(), ["destino"] = Id(), ["revisao_origem"] = Revisao(), ["revisao_destino"] = Revisao(), ["tipo"] = EnumCampo<Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao>() }, ["origem", "destino", "revisao_origem", "revisao_destino", "tipo"]),
        new("brain_listar_relacoes", "Relações autorizadas de um conhecimento, limite máximo 25.", true, false, new() { ["id"] = Id(), ["limite"] = Revisao() }, ["id"]),
        new("brain_obter_contexto_de_trabalho", "Snapshot operacional atual, distinto de conhecimento.", true, false, new(), []),
        new("brain_atualizar_contexto_de_trabalho", "Atualiza snapshot operacional com revisão esperada. Não armazena transcript nem raciocínio privado e não cria Conhecimento.", false, true,
            new() { ["revisao"] = Revisao(), ["objetivo"] = Texto(2000), ["tarefa"] = Texto(2000), ["progresso"] = Texto(2000), ["ultimo_resultado"] = Texto(2000),
                ["referencias"] = Tags(), ["pendencias"] = Tags(), ["proximos_passos"] = Tags() }, ["revisao", "objetivo", "tarefa", "progresso", "ultimo_resultado"])
    ];
    public static IReadOnlyList<string> FerramentasDeLeitura => Ferramentas.Where(x => x.Leitura).Select(x => x.Nome).ToArray();
    public static JsonObject Lista() => new() { ["tools"] = new JsonArray(Ferramentas.Select(f => (JsonNode)new JsonObject
    {
        ["name"] = f.Nome, ["description"] = f.Descricao,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = f.Propriedades.DeepClone(), ["required"] = new JsonArray(f.Obrigatorios.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["additionalProperties"] = false },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = f.Leitura, ["destructiveHint"] = f.Destrutiva, ["openWorldHint"] = false }
    }).ToArray()) };
    public static void Validar(string nome, JsonElement args)
    {
        var f = Ferramentas.SingleOrDefault(x => x.Nome == nome) ?? throw new ArgumentException("Ferramenta desconhecida.");
        if (args.ValueKind != JsonValueKind.Object || args.GetRawText().Length > MaximoEntrada) throw new ArgumentException("Argumentos inválidos.");
        foreach (var p in args.EnumerateObject())
            if (!f.Propriedades.ContainsKey(p.Name)) throw new ArgumentException("Argumento não permitido.");
        foreach (var requerido in f.Obrigatorios)
            if (!args.TryGetProperty(requerido, out _)) throw new ArgumentException("Argumento obrigatório ausente.");
    }
    public static JsonObject Resultado(object? resultado, bool erro = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(resultado, JsonOptions) }), ["isError"] = erro
    };
    public static async Task<int> ExecutarProcessoAsync(string[] args)
    {
        if (args.Length != 3 || args[1] != "--pipe" || !args[2].StartsWith("dante-brain-", StringComparison.Ordinal)) return 1;
        using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await pipe.ConnectAsync(timeout.Token); } catch { return 1; }
        using var leitor = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var escritor = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await ExecutarAsync(Console.In, Console.Out, async (nome, argumentos, ct) =>
        {
            await escritor.WriteLineAsync(JsonSerializer.Serialize(new { nome, argumentos }).AsMemory(), ct);
            var resposta = await LerLinhaAsync(leitor, MaximoResposta, ct) ?? throw new IOException();
            return JsonNode.Parse(resposta)!.AsObject();
        });
        return 0;
    }
    public static async Task ExecutarAsync(TextReader entrada, TextWriter saida,
        Func<string, JsonElement, CancellationToken, Task<JsonObject>> chamar, CancellationToken ct = default)
    {
        while (await LerLinhaAsync(entrada, MaximoEntrada, ct) is { } linha)
        {
            JsonObject resposta;
            JsonNode? id = null;
            try
            {
                using var documento = JsonDocument.Parse(linha); var pedido = documento.RootElement;
                if (!pedido.TryGetProperty("id", out var identificador)) continue;
                id = JsonNode.Parse(identificador.GetRawText());
                var metodo = pedido.GetProperty("method").GetString();
                JsonObject resultado;
                if (metodo == "initialize") resultado = new() { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = Nome, ["version"] = "1.0.0" },
                    ["instructions"] = "Use o escopo autorizado. Capture itens individualmente e reporte duplicidades. Aprovação da ferramenta não confirma negócio. Propostas de consolidação/relação/descarte exigem confirmar no Telegram. Nunca capture transcript ou chain-of-thought." };
                else if (metodo == "ping") resultado = new();
                else if (metodo == "tools/list") resultado = Lista();
                else if (metodo == "tools/call")
                {
                    var parametros = pedido.GetProperty("params"); var nome = parametros.GetProperty("name").GetString()!;
                    var argumentos = parametros.TryGetProperty("arguments", out var a) ? a : JsonSerializer.SerializeToElement(new { });
                    Validar(nome, argumentos); resultado = await chamar(nome, argumentos, ct);
                }
                else throw new ArgumentException("Método desconhecido.");
                resposta = new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = resultado };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { resposta = new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Resultado("Brain indisponível ou entrada/escopo/revisão inválidos. Refaça a consulta.", true) }; }
            var json = resposta.ToJsonString();
            if (json.Length > MaximoResposta) json = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
                ["result"] = Resultado("Resposta excede o limite; reduza a consulta.", true) }.ToJsonString();
            await saida.WriteLineAsync(json.AsMemory(), ct); await saida.FlushAsync(ct);
        }
    }
    // Não deixa ReadLineAsync alocar uma linha ilimitada enviada pelo cliente local/modelo.
    public static async Task<string?> LerLinhaAsync(TextReader leitor, int maximo, CancellationToken ct)
    {
        var buffer = new char[1]; var linha = new StringBuilder();
        while (await leitor.ReadAsync(buffer.AsMemory(), ct) != 0)
        {
            if (buffer[0] == '\n') return linha.ToString();
            if (linha.Length == maximo) throw new ArgumentException("Mensagem excede o limite.");
            if (buffer[0] != '\r') linha.Append(buffer[0]);
        }
        return linha.Length == 0 ? null : linha.ToString();
    }
}
