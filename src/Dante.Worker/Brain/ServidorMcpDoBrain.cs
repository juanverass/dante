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
    // Excedente descartado: até 1 Mi caracteres e 30 s a partir do primeiro caractere além do limite (#251).
    public const int MaximoDescarte = 1_048_576;
    public static readonly TimeSpan JanelaDeDescarte = TimeSpan.FromSeconds(30);
    public const int MaximoResposta = 60_000;
    // Paginação: os mesmos limites são validados em runtime por OperacoesMcpDoBrain.
    public const int LimiteMaximo = 25;
    public const int LimitePadrao = 10;
    public const int DeslocamentoMaximo = 10_000;
    public sealed record Ferramenta(string Nome, string Descricao, bool Leitura, bool Destrutiva, JsonObject Propriedades, string[] Obrigatorios);
    private static JsonObject Campo(string tipo, string? descricao = null) => new() { ["type"] = tipo, ["description"] = descricao ?? "" };
    private static JsonObject EnumCampo<T>(T? excluir = null) where T : struct, Enum => new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames<T>().Where(x => x != excluir?.ToString()).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
    private static JsonObject Id() => Campo("string", "Identificador retornado por outra ferramenta; nunca solicite GUID ao usuário.");
    private static JsonObject Revisao() => Campo("integer", "Revisão esperada retornada na consulta.");
    private static JsonObject Limite() => new() { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = LimiteMaximo,
        ["description"] = $"Quantidade máxima de itens por página (1 a {LimiteMaximo}; padrão {LimitePadrao})." };
    private static JsonObject Deslocamento() => new() { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = DeslocamentoMaximo,
        ["description"] = $"Quantidade de itens a pular para paginar (0 a {DeslocamentoMaximo}; padrão 0)." };
    private static JsonObject Texto(int maximo) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximo };
    private static JsonObject TextoDaBusca()
    {
        var campo = Texto(2000);
        campo["description"] = "Consulta preservada, até 2000 caracteres. Na busca lexical em português: AND implícito entre termos, or para alternativas, aspas para frase; hífen é aceito, mas não garante exclusão na seleção atual por pontuação. Stopwords são do PostgreSQL; não há corte em 16 termos. Diferente do contexto automático do Telegram, que extrai até 16 termos e combina por OR.";
        return campo;
    }
    private static JsonObject Tags() => new() { ["type"] = "array", ["maxItems"] = 50, ["items"] = Texto(100) };
    private static JsonObject Captura(bool correcao = false)
    {
        var p = new JsonObject { ["titulo"] = Texto(300), ["conteudo"] = Texto(10_000), ["tags"] = Tags(),
            ["tipo"] = EnumCampo<Dante.Domain.Conhecimentos.TipoDeConhecimento>(),
            ["natureza"] = EnumCampo<Dante.Domain.CapturaDeConhecimento.NaturezaDoConteudo>(Dante.Domain.CapturaDeConhecimento.NaturezaDoConteudo.Desconhecida),
            ["sensibilidade"] = EnumCampo<Dante.Domain.Conhecimentos.Sensibilidade>(), ["justificativa"] = Texto(2000) };
        if (correcao) { p["id"] = Id(); p["revisao"] = Revisao(); }
        return p;
    }
    public static readonly IReadOnlyList<Ferramenta> Ferramentas =
    [
        new("brain_obter_escopo", "Espaço/projeto autorizado da sessão. Não altera escopo.", true, false, new(), []),
        new("brain_buscar_conhecimento", "Busca permitida em conhecimentos e fontes do escopo; candidatos pendentes não entram (use brain_listar_candidatos). Sem modelo de embedding a busca é lexical e exige todos os termos não descartados pelo dicionário português (AND implícito); use \"or\" entre termos alternativos, aspas para frase. Hífen é aceito, mas não garante exclusão na seleção atual por pontuação. O texto é preservado, diferente do contexto automático do Telegram (extração de até 16 termos com OR). Natureza filtra a autoria de origem do conhecimento e exclui fontes brutas. A resposta inclui natureza de origem, natureza vigente, status e rótulo. Tags filtram, mas não substituem o texto. Consulte equivalentes antes de capturar e reporte possíveis duplicidades. " + ContratoDoBrainParaAgentes.RegraDaConfirmacao, true, false,
            new() { ["texto"] = TextoDaBusca(), ["natureza"] = EnumCampo<Dante.Domain.CapturaDeConhecimento.NaturezaDoConteudo>(), ["tipo"] = EnumCampo<Dante.Domain.Conhecimentos.TipoDeConhecimento>(), ["tags"] = Tags(), ["limite"] = Limite(), ["deslocamento"] = Deslocamento() }, ["texto"]),
        new("brain_mostrar_origem", "Proveniência de candidato ou conhecimento permitido. " + ContratoDoBrainParaAgentes.RegraDaConfirmacao, true, false, new() { ["id"] = Id(), ["origem"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("candidato", "conhecimento") } }, ["id", "origem"]),
        new("brain_capturar_conhecimento", "Cria um único candidato pendente. Preserve título/conteúdo/tags e o tipo semântico informado. Use natureza ConclusaoDoAgente para conclusões do agente: permanecem inferidas, nunca ditas pelo usuário.", false, false, Captura(), ["conteudo", "natureza", "justificativa"]),
        new("brain_atualizar_conhecimento", "PREPARA correção do conhecimento existente, sem criar outro ativo. Exige revisão e confirmação Telegram; tipo opcional: preserve o atual quando omitido; a correção não herda a confirmação anterior.", false, true, Captura(true), ["id", "revisao", "conteudo", "natureza", "justificativa"]),
        new("brain_consolidar_duplicatas", "PREPARA consolidação explícita: conserva o destino, incorpora proveniências e substitui duplicatas. Exige confirmar no Telegram e revisões de todos os alvos.", false, true,
            new() { ["id"] = Id(), ["revisao"] = Revisao(), ["duplicatas"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 20,
                ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["id"] = Id(), ["revisao"] = Revisao() }, ["required"] = new JsonArray("id", "revisao"), ["additionalProperties"] = false } } }, ["id", "revisao", "duplicatas"]),
        new("brain_listar_candidatos", "Candidatos pendentes do escopo, com revisão. Tags opcionais exigem todas as tags da revisão atual, sem distinguir maiúsculas. Texto opcional é substring literal no título ou conteúdo atual, sem distinguir maiúsculas, combinado com tags por AND. Sem filtros preserva a listagem. Paginação por deslocamento, limite máximo 25.", true, false,
            new() { ["tags"] = Tags(), ["texto"] = Texto(2000), ["limite"] = Limite(), ["deslocamento"] = Deslocamento() }, []),
        new("brain_corrigir_candidato", "Corrige candidato pendente com revisão esperada, sem redefinir natureza ou escopo.", false, true, Captura(true), ["id", "revisao", "conteudo", "natureza", "justificativa"]),
        new("brain_confirmar_candidato", "PREPARA autorização de registro do candidato. Não consolida pela aprovação MCP. Mostre o candidato, explique o efeito e peça autorização para registrar; o usuário autoriza enviando confirmar no Telegram, comando operacional que não confirma o conteúdo de conclusões do agente (continuam Inferido); uma proposta de cada vez, válida por 5 minutos. " + ContratoDoBrainParaAgentes.RegraDaConfirmacao, false, false, new() { ["id"] = Id(), ["revisao"] = Revisao() }, ["id", "revisao"]),
        new("brain_cancelar_candidato", "PREPARA descarte auditado; exige confirmação de negócio pelo Telegram.", false, true, new() { ["id"] = Id(), ["revisao"] = Revisao() }, ["id", "revisao"]),
        new("brain_criar_relacao", "PREPARA relação explícita entre dois conhecimentos autorizados com revisões. Peça confirmar no Telegram; sugestões não são relações persistidas.", false, false,
            new() { ["origem"] = Id(), ["destino"] = Id(), ["revisao_origem"] = Revisao(), ["revisao_destino"] = Revisao(), ["tipo"] = EnumCampo<Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao>() }, ["origem", "destino", "revisao_origem", "revisao_destino", "tipo"]),
        new("brain_vincular_candidatos", "Registra a INTENÇÃO de ResolvidoPor entre dois candidatos pendentes (incidente: candidato Incidente; solucao: candidato Solucao), com revisões. Não cria relação, não confirma conteúdo e não pede confirmação: a relação só é persistida quando o usuário autorizar o registro dos dois candidatos no Telegram, uma única vez. Descartar ou rejeitar um lado cancela a intenção. Repetir o mesmo par devolve a intenção existente. " + ContratoDoBrainParaAgentes.RegraDaConfirmacao, false, false,
            new() { ["incidente"] = Id(), ["revisao_incidente"] = Revisao(), ["solucao"] = Id(), ["revisao_solucao"] = Revisao() }, ["incidente", "revisao_incidente", "solucao", "revisao_solucao"]),
        new("brain_listar_relacoes", "Relações autorizadas de um conhecimento, limite máximo 25. Aceita somente ID de conhecimento ativo (retornado por brain_buscar_conhecimento); candidatos pendentes não têm relações.", true, false, new() { ["id"] = Id(), ["limite"] = Limite() }, ["id"]),
        new("brain_registrar_evidencia", "Registra UMA evidência de teste/validação como fonte bruta (DocumentoFonte Markdown) no escopo da sessão, com autoria do agente. Não cria candidato nem conhecimento e não confirma nada; não edita nem remove fontes. Use somente resultados operacionais selecionados: sem credenciais, transcript ou raciocínio privado. Sensibilidade Publico, Pessoal (padrão) ou Trabalho. Retorna a referência fonte:<id>@<rev> para usar em referencias do snapshot. Mesmo conteúdo repetido gera outro registro e é sinalizado em possivelDuplicidade.", false, false,
            new() { ["titulo"] = Texto(Dante.Domain.DocumentosFonte.DocumentoFonte.TamanhoMaximoDoTitulo),
                ["conteudo"] = Texto(Dante.Application.DocumentosFonte.DocumentoFonteAppService.TamanhoMaximoDaEvidencia), ["tags"] = Tags(),
                ["sensibilidade"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Publico", "Pessoal", "Trabalho") },
                ["justificativa"] = Texto(Dante.Domain.DocumentosFonte.DocumentoFonte.TamanhoMaximoDaJustificativa) }, ["titulo", "conteudo"]),
        new("brain_obter_contexto_de_trabalho", "Snapshot operacional atual, distinto de conhecimento.", true, false, new(), []),
        new("brain_atualizar_contexto_de_trabalho", "Atualiza snapshot operacional com revisão esperada. Decisões do usuário devem ser referenciadas em ids_decisoes_confirmadas, não afirmadas em texto livre: use IDs de conhecimentos Decisao já confirmados no Telegram, válidos e permitidos no mesmo escopo. A lista informada substitui a anterior; [] limpa; omitir preserva. Não armazena transcript nem raciocínio privado e não cria Conhecimento.", false, true,
            new() { ["revisao"] = Revisao(), ["objetivo"] = Texto(2000), ["tarefa"] = Texto(2000), ["progresso"] = Texto(2000), ["ultimo_resultado"] = Texto(2000),
                ["ids_decisoes_confirmadas"] = new JsonObject { ["type"] = "array", ["maxItems"] = 20,
                    ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid", ["maxLength"] = 40 },
                    ["description"] = "Lista completa de até 20 IDs de decisões confirmadas, válidas, do escopo e compatíveis com a sessão e a sensibilidade do snapshot. Duplicatas são deduplicadas ao salvar. [] remove todas; omitir mantém a lista atual. IDs vêm da busca de conhecimentos, nunca do usuário nem de candidatos." },
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
        while (await LerOuDescartarLinhaAsync(entrada, MaximoEntrada, ct) is { } lida)
        {
            JsonObject resposta;
            JsonNode? id = null;
            try
            {
                // Linha acima do limite já foi descartada sem acumular; responde e segue lendo (id desconhecido).
                // Excedente sem quebra dentro dos limites de descarte: responde e encerra o servidor de forma controlada.
                if (lida.Excedida) throw ErroDoBrainMcp.Entrada(lida.Abortada ? ErrosDoBrainMcp.DetalheEntradaAbortada : ErrosDoBrainMcp.DetalheEntradaGrande);
                using var documento = JsonDocument.Parse(lida.Texto!); var pedido = documento.RootElement;
                if (!pedido.TryGetProperty("id", out var identificador)) continue;
                id = JsonNode.Parse(identificador.GetRawText());
                var metodo = pedido.GetProperty("method").GetString();
                JsonObject resultado;
                if (metodo == "initialize") resultado = new() { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = Nome, ["version"] = "1.0.0" },
                    ["instructions"] = ContratoDoBrainParaAgentes.Instrucoes };
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
            // Schema/JSON inválido é entrada_invalida; Worker inacessível pelo pipe é indisponivel (#251).
            catch (Exception ex) when (ex is not OperationCanceledException)
            { resposta = new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = ErrosDoBrainMcp.Resultado(ex) }; }
            var json = resposta.ToJsonString();
            if (json.Length > MaximoResposta) json = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
                ["result"] = ErrosDoBrainMcp.Resultado(CategoriaDeErroMcp.EntradaInvalida, ErrosDoBrainMcp.DetalheRespostaGrande) }.ToJsonString();
            await saida.WriteLineAsync(json.AsMemory(), ct); await saida.FlushAsync(ct);
            if (lida.Abortada) return;
        }
    }
    // Abortada: o excedente não terminou dentro de `maximoDescarte` caracteres nem da janela; o chamador encerra o canal.
    public readonly record struct LinhaLida(string? Texto, bool Excedida, bool Abortada = false);
    // Como LerLinhaAsync, mas a linha acima do limite é consumida até o fim e descartada sem acumular:
    // memória limitada a `maximo` e o canal continua alinhado na próxima linha (#251). O descarte é limitado
    // em quantidade e em tempo, para que um cliente sem quebra de linha não prenda a sessão. Null no fim da entrada.
    public static async Task<LinhaLida?> LerOuDescartarLinhaAsync(TextReader leitor, int maximo, CancellationToken ct,
        int maximoDescarte = MaximoDescarte, TimeSpan? janela = null)
    {
        var buffer = new char[1]; var linha = new StringBuilder(); var leuAlgo = false; var recebidos = 0;
        while (await leitor.ReadAsync(buffer.AsMemory(), ct) != 0)
        {
            leuAlgo = true;
            if (buffer[0] == '\n') return new(linha.ToString(), false);
            // Conta também CR antes da normalização, para limitar o fluxo recebido.
            if (recebidos++ == maximo) return await DescartarAsync(leitor, maximoDescarte, janela ?? JanelaDeDescarte, ct);
            if (buffer[0] != '\r') linha.Append(buffer[0]);
        }
        return !leuAlgo || linha.Length == 0 ? null : new(linha.ToString(), false);
    }
    private static async Task<LinhaLida> DescartarAsync(TextReader leitor, int maximoDescarte, TimeSpan janela, CancellationToken ct)
    {
        // O caractere que estourou o limite já foi consumido: é o primeiro excedente. A quebra final não conta.
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct); prazo.CancelAfter(janela);
        var buffer = new char[1]; var excedentes = 1;
        try
        {
            while (await leitor.ReadAsync(buffer.AsMemory(), prazo.Token) != 0)
            {
                if (buffer[0] == '\n') return new(null, true);
                if (++excedentes > maximoDescarte) return new(null, true, true);
            }
            return new(null, true); // fim da entrada: responde e o laço termina na próxima leitura
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(null, true, true); }
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
