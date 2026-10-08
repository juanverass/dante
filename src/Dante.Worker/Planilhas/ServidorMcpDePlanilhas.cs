using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application;
using Dante.Application.Planilhas;
using Dante.Infrastructure;

namespace Dante.Worker.Planilhas;

// Servidor MCP (stdio, JSON-RPC por linha) que expõe a capacidade genérica de planilhas às sessões do Claude e do
// Codex (#224, AD-55). É adapter de entrada: traduz tools/call para PlanilhasAppService e devolve texto compacto com
// coordenadas, para o agente interpretar e a escrita final ser exata. Leituras são anotadas readOnlyHint e rodam sem
// aprovação; escritas seguem o modo de aprovação da sessão (as CLIs pedem aprovação ao D.A.N.T.E.).
public sealed class ServidorMcpDePlanilhas(IServiceScopeFactory scopes, OrigemDaSolicitacao origem)
{
    public const string Argumento = "--mcp-planilhas";
    public const string Nome = "dante_planilhas";
    private const string Protocolo = "2025-06-18";
    private const int MaximoDaResposta = 60_000;

    public static readonly IReadOnlyList<string> FerramentasDeLeitura =
        ["listar_planilhas", "descrever_planilha", "ler_intervalo", "buscar_na_planilha"];

    private const string Instrucoes = ContratoDePlanilhasParaAgentes.Instrucoes + "\n" +
        "Ferramentas genéricas de planilhas do D.A.N.T.E. (Google Sheets e XLSX cadastrado no Drive). Trabalhe progressivamente: listar_planilhas → " +
        "descrever_planilha (abas, área usada, mesclagens, regiões anotadas) → ler_intervalo de uma região pequena ou " +
        "buscar_na_planilha → expandir só a região relevante → escrever no alvo resolvido. Não leia a aba inteira sem " +
        "necessidade. Planilhas podem ter vários blocos, cabeçalhos em posições diferentes e células mescladas: o valor " +
        "de uma mesclagem vive na célula superior esquerda. Toda escrita usa coordenadas exatas; informe valores_esperados " +
        "com o que você leu. Se houver mais de um alvo plausível, pergunte ao usuário antes de escrever: nunca escolha em " +
        "silêncio. Diga ao usuário o valor anterior e o novo. Exclusão de linhas/abas, limpeza em massa e mudança de " +
        "estrutura não são suportadas. Respeite as observações da leitura: XLSX usa valores brutos e o cache salvo das " +
        "fórmulas, que pode estar desatualizado. Para números no XLSX, envie número JSON; texto fica literal. Não prometa " +
        "recálculo de fórmulas ou reprodução exata do valor formatado pelo editor.";

    // Processo filho lançado pela CLI do agente: compõe Application/Infrastructure sem Telegram nem host, e só escreve
    // JSON-RPC no stdout (logs ficam fora dele).
    public static async Task<int> ExecutarProcessoAsync(string[] args)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
        await using var provider = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(configuration)
            .BuildServiceProvider();
        var origem = new OrigemDaSolicitacao("mcp", Opcao(args, "--origem"), Opcao(args, "--agente"));
        using var entrada = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var saida = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        await new ServidorMcpDePlanilhas(provider.GetRequiredService<IServiceScopeFactory>(), origem)
            .ExecutarAsync(entrada, saida, CancellationToken.None);
        return 0;
    }

    public async Task ExecutarAsync(TextReader entrada, TextWriter saida, CancellationToken cancellationToken)
    {
        while (await entrada.ReadLineAsync(cancellationToken) is { } linha)
        {
            if (string.IsNullOrWhiteSpace(linha)) continue;
            JsonObject? mensagem;
            try { mensagem = JsonNode.Parse(linha) as JsonObject; }
            catch (JsonException) { mensagem = null; }
            JsonObject? resposta;
            try
            {
                resposta = mensagem is null ? Erro(null, -32700, "JSON inválido.") : await ResponderAsync(mensagem, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Uma falha inesperada responde só àquele pedido; o servidor segue atendendo a sessão.
                resposta = Erro(mensagem?["id"]?.DeepClone(), -32603, $"Falha interna ({exception.GetType().Name}).");
            }
            if (resposta is null) continue;
            await saida.WriteLineAsync(resposta.ToJsonString());
            await saida.FlushAsync(cancellationToken);
        }
    }

    // Notificação (sem id) não tem resposta.
    internal async Task<JsonObject?> ResponderAsync(JsonObject mensagem, CancellationToken cancellationToken)
    {
        var id = mensagem["id"]?.DeepClone();
        var metodo = mensagem["method"] is JsonValue m && m.TryGetValue<string>(out var nome) ? nome : null;
        if (id is null) return null;
        var parametros = mensagem["params"] as JsonObject ?? [];
        return metodo switch
        {
            "initialize" => Resultado(id, new JsonObject
            {
                ["protocolVersion"] = Texto(parametros, "protocolVersion") ?? Protocolo,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = Nome, ["version"] = "1.0" },
                ["instructions"] = Instrucoes
            }),
            "ping" => Resultado(id, []),
            "tools/list" => Resultado(id, new JsonObject { ["tools"] = Ferramentas() }),
            "tools/call" => Resultado(id, await ChamarAsync(Texto(parametros, "name"), parametros["arguments"] as JsonObject ?? [],
                cancellationToken)),
            _ => Erro(id, -32601, $"Método {metodo} não suportado.")
        };
    }

    private async Task<JsonObject> ChamarAsync(string? ferramenta, JsonObject argumentos, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var planilhas = scope.ServiceProvider.GetRequiredService<PlanilhasAppService>();
        try
        {
            var texto = ferramenta switch
            {
                "listar_planilhas" => await ListarAsync(planilhas, cancellationToken),
                "cadastrar_planilha" => FormatarCadastro(await planilhas.CadastrarAsync(Exigir(argumentos, "alias"),
                    Exigir(argumentos, "url_ou_id"), Texto(argumentos, "descricao"), cancellationToken), "Planilha cadastrada"),
                "descrever_planilha" => FormatarDescricao(await planilhas.DescreverAsync(Exigir(argumentos, "planilha"), cancellationToken)),
                "ler_intervalo" => FormatarLeitura(await planilhas.LerAsync(Exigir(argumentos, "planilha"), Exigir(argumentos, "intervalo"),
                    Inteiro(argumentos, "limite_de_celulas"), cancellationToken)),
                "buscar_na_planilha" => FormatarBusca(await planilhas.BuscarAsync(Exigir(argumentos, "planilha"), Exigir(argumentos, "texto"),
                    Texto(argumentos, "aba"), Texto(argumentos, "intervalo"), Inteiro(argumentos, "limite"), cancellationToken)),
                "atualizar_celulas" => FormatarEscrita(await planilhas.AtualizarAsync(Escrita(argumentos), origem, cancellationToken)),
                "atualizar_por_referencia" => FormatarEscrita(await planilhas.AtualizarPorReferenciaAsync(Referencia(argumentos), origem,
                    cancellationToken)),
                "adicionar_linha" => FormatarEscrita(await planilhas.AdicionarLinhaAsync(new AdicaoDeLinhaDto
                {
                    Planilha = Exigir(argumentos, "planilha"),
                    Intervalo = Exigir(argumentos, "intervalo_da_tabela"),
                    Valores = (argumentos["valores"] as JsonArray ?? throw new ArgumentException("Informe valores."))
                        .Select(Valor).ToArray()
                }, origem, cancellationToken)),
                "anotar_regiao" => FormatarCadastro(await planilhas.AnotarRegiaoAsync(Exigir(argumentos, "planilha"),
                    Exigir(argumentos, "nome"), Exigir(argumentos, "intervalo"), Texto(argumentos, "descricao"), cancellationToken),
                    "Região anotada"),
                _ => throw new ArgumentException($"Ferramenta desconhecida: {ferramenta}.")
            };
            return Conteudo(texto, erro: false);
        }
        catch (FalhaDePlanilhaException exception)
        {
            var texto = new StringBuilder(exception.Message);
            if (exception.Candidatos.Count > 0)
            {
                texto.Append("\nCandidatos:");
                foreach (var candidato in exception.Candidatos) texto.Append("\n- ").Append(FormatarOcorrencia(candidato));
            }
            return Conteudo(texto.ToString(), erro: true);
        }
        catch (ArgumentException exception)
        {
            return Conteudo("Pedido inválido: " + exception.Message, erro: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Conteudo("Falha local ao acessar o cadastro ou a credencial de planilhas.", erro: true);
        }
    }

    private static async Task<string> ListarAsync(PlanilhasAppService planilhas, CancellationToken cancellationToken)
    {
        var conexao = await planilhas.ObterConexaoAsync(cancellationToken);
        var cadastradas = await planilhas.ListarAsync(cancellationToken);
        var texto = new StringBuilder(FormatarConexao(conexao)).Append('\n');
        if (cadastradas.Count == 0) return texto.Append("Nenhuma planilha cadastrada.").ToString();
        texto.Append("Planilhas cadastradas:");
        foreach (var planilha in cadastradas) texto.Append('\n').Append(FormatarCadastro(planilha, null));
        return texto.ToString();
    }

    internal static string FormatarConexao(EstadoDaConexaoDto conexao) => conexao switch
    {
        { Conectada: true } => $"{conexao.Provedor}: conectado{(conexao.Conta is null ? "" : $" ({conexao.Conta})")}.",
        { Problema: { } problema } => $"{conexao.Provedor}: desconectado — {problema}",
        { Configurada: false } => $"{conexao.Provedor}: integração não configurada no D.A.N.T.E.",
        _ => $"{conexao.Provedor}: nenhuma conta conectada."
    };

    internal static string FormatarCadastro(PlanilhaCadastradaDto planilha, string? titulo)
    {
        var texto = new StringBuilder(titulo is null ? "- " : titulo + ": ")
            .Append(planilha.Alias).Append(" — ").Append(planilha.Titulo ?? "(sem título)").Append(" [").Append(planilha.IdDaPlanilha).Append(']');
        if (planilha.Descricao is { } descricao) texto.Append("\n  ").Append(descricao);
        foreach (var regiao in planilha.Regioes)
            texto.Append("\n  região ").Append(regiao.Nome).Append(" = ").Append(regiao.Intervalo)
                .Append(regiao.Descricao is null ? "" : " — " + regiao.Descricao);
        return texto.ToString();
    }

    private static string FormatarDescricao(DescricaoDaPlanilhaDto descricao)
    {
        var planilha = descricao.Planilha;
        var texto = new StringBuilder(FormatarCadastro(descricao.Cadastro, "Planilha")).Append('\n')
            .Append("Título: ").Append(planilha.Titulo);
        if (planilha.Localidade is not null) texto.Append(" | localidade ").Append(planilha.Localidade);
        if (planilha.FusoHorario is not null) texto.Append(" | fuso ").Append(planilha.FusoHorario);
        if (planilha.Observacao is not null) texto.Append('\n').Append(planilha.Observacao);
        texto.Append("\nAbas:");
        foreach (var aba in planilha.Abas)
        {
            texto.Append("\n- '").Append(aba.Titulo).Append("' (").Append(aba.Tipo == "GRID" ? "grade" : aba.Tipo.ToLowerInvariant())
                .Append(' ').Append(aba.Linhas).Append('x').Append(aba.Colunas)
                .Append(aba.AreaUsada is null ? ", sem dados" : ", área usada " + aba.AreaUsada).Append(')');
            if (aba.Mesclagens.Count > 0)
                texto.Append(" mesclagens: ").Append(string.Join(", ", aba.Mesclagens.Take(40)))
                    .Append(aba.Mesclagens.Count > 40 ? $" (+{aba.Mesclagens.Count - 40})" : "");
        }
        return Limitar(texto.ToString());
    }

    private static string FormatarLeitura(IntervaloDaPlanilhaDto leitura)
    {
        var texto = new StringBuilder(leitura.Intervalo).Append(": ").Append(leitura.Celulas.Count(c => !c.EstaVazia))
            .Append(" célula(s) com conteúdo");
        if (leitura.Observacao is not null) texto.Append('\n').Append(leitura.Observacao);
        foreach (var celula in leitura.Celulas) texto.Append('\n').Append(FormatarCelula(celula));
        if (leitura.Mesclagens.Count > 0) texto.Append("\nMesclagens: ").Append(string.Join(", ", leitura.Mesclagens));
        if (leitura.Truncado) texto.Append("\n[truncado pelo limite de células: leia a região seguinte ou uma menor]");
        return Limitar(texto.ToString());
    }

    private static string FormatarBusca(ResultadoDaBuscaNaPlanilhaDto busca)
    {
        var texto = new StringBuilder($"{busca.Ocorrencias.Count} ocorrência(s) de \"{busca.Termo}\" em ")
            .Append(string.Join(", ", busca.AbasConsultadas.Select(a => $"'{a}'"))).Append(" (exatas primeiro)");
        if (busca.Observacao is not null) texto.Append('\n').Append(busca.Observacao);
        foreach (var ocorrencia in busca.Ocorrencias) texto.Append("\n- ").Append(FormatarOcorrencia(ocorrencia));
        if (busca.Truncado) texto.Append("\n[há mais ocorrências: restrinja a aba/intervalo ou aumente o limite]");
        return Limitar(texto.ToString());
    }

    private static string FormatarOcorrencia(OcorrenciaNaPlanilhaDto ocorrencia) =>
        $"'{ocorrencia.Aba}'!{ocorrencia.Celula.Endereco} = {Citar(ocorrencia.Celula.ValorExibido)} | linha {ocorrencia.Celula.Linha}: " +
        string.Join("; ", ocorrencia.Linha.Select(c => $"{c.Endereco}={Citar(c.ValorExibido)}"));

    private static string FormatarEscrita(ResultadoDaEscritaDto escrita)
    {
        var texto = new StringBuilder("Escrita aplicada na planilha ").Append(escrita.Planilha).Append(": ")
            .Append(string.Join(", ", escrita.Intervalos));
        foreach (var celula in escrita.Celulas)
            texto.Append("\n'").Append(celula.Aba).Append("'!").Append(celula.Endereco).Append(": ")
                .Append(Citar(celula.ValorAnterior)).Append(celula.FormulaAnterior is null ? "" : $" (fórmula {celula.FormulaAnterior})")
                .Append(" → ").Append(Citar(celula.ValorNovo));
        return Limitar(texto.ToString());
    }

    private static string FormatarCelula(CelulaDaPlanilhaDto celula)
    {
        var texto = new StringBuilder(celula.Endereco).Append(" = ").Append(Citar(celula.ValorExibido));
        var bruto = celula.ValorBruto?.ToString();
        if (celula.Formula is not null) texto.Append(" (fórmula ").Append(celula.Formula).Append(')');
        if (!string.IsNullOrEmpty(bruto) && bruto != celula.ValorExibido) texto.Append(" (bruto ").Append(bruto).Append(')');
        if (celula.Mesclagem is not null) texto.Append(" [mesclagem ").Append(celula.Mesclagem).Append(']');
        return texto.ToString();
    }

    private static string Citar(string? valor) => valor is null or "" ? "(vazio)" : JsonSerializer.Serialize(valor,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Limitar(string texto) => texto.Length <= MaximoDaResposta ? texto :
        texto[..MaximoDaResposta] + "\n[resposta cortada: leia uma região menor]";

    private static EscritaNaPlanilhaDto Escrita(JsonObject argumentos) => new()
    {
        Planilha = Exigir(argumentos, "planilha"),
        PermitirSobrescreverFormulas = Booleano(argumentos, "permitir_sobrescrever_formulas"),
        Alteracoes = (argumentos["alteracoes"] as JsonArray ?? throw new ArgumentException("Informe alteracoes."))
            .OfType<JsonObject>().Select(alteracao => new AlteracaoDeIntervaloDto
            {
                Intervalo = Exigir(alteracao, "intervalo"),
                Valores = Matriz(alteracao["valores"], Valor),
                ValoresEsperados = alteracao["valores_esperados"] is null ? null :
                    Matriz(alteracao["valores_esperados"], v => v is null ? null : Valor(v).ToString())
            }).ToArray()
    };

    private static EscritaPorReferenciaDto Referencia(JsonObject argumentos) => new()
    {
        Planilha = Exigir(argumentos, "planilha"),
        Referencia = Exigir(argumentos, "referencia"),
        Aba = Texto(argumentos, "aba"),
        Intervalo = Texto(argumentos, "intervalo"),
        Coluna = Texto(argumentos, "coluna"),
        DeslocamentoDeColunas = Inteiro(argumentos, "deslocamento_de_colunas") ?? 0,
        DeslocamentoDeLinhas = Inteiro(argumentos, "deslocamento_de_linhas") ?? 0,
        Valor = Valor(argumentos["valor"]),
        ValorEsperado = argumentos["valor_esperado"] is { } esperado ? Valor(esperado).ToString() : null,
        PermitirSobrescreverFormulas = Booleano(argumentos, "permitir_sobrescrever_formulas")
    };

    private static IReadOnlyList<IReadOnlyList<T>> Matriz<T>(JsonNode? no, Func<JsonNode?, T> converter) =>
        (no as JsonArray ?? throw new ArgumentException("Informe uma lista de linhas (lista de listas)."))
        .Select(linha => (IReadOnlyList<T>)(linha as JsonArray ?? throw new ArgumentException("Cada linha deve ser uma lista."))
            .Select(converter).ToArray()).ToArray();

    private static ValorDeCelula Valor(JsonNode? no) => no switch
    {
        null => ValorDeCelula.Vazio,
        JsonValue valor when valor.GetValueKind() == JsonValueKind.String => ValorDeCelula.DeTexto(valor.GetValue<string>()),
        JsonValue valor when valor.GetValueKind() == JsonValueKind.Number =>
            ValorDeCelula.DeNumero(double.Parse(valor.ToJsonString(), CultureInfo.InvariantCulture)),
        JsonValue valor when valor.GetValueKind() is JsonValueKind.True or JsonValueKind.False => ValorDeCelula.DeBooleano(valor.GetValue<bool>()),
        _ => throw new ArgumentException("Valores devem ser texto, número, booleano ou null.")
    };

    private static string Exigir(JsonObject argumentos, string nome) =>
        Texto(argumentos, nome) is { Length: > 0 } texto ? texto : throw new ArgumentException($"Informe {nome}.");

    private static string? Texto(JsonObject? argumentos, string nome) => argumentos?[nome] switch
    {
        JsonValue valor when valor.GetValueKind() == JsonValueKind.String => valor.GetValue<string>(),
        JsonValue valor when valor.GetValueKind() == JsonValueKind.Number => valor.ToJsonString(),
        _ => null
    };

    private static int? Inteiro(JsonObject argumentos, string nome) => argumentos[nome] switch
    {
        null => null,
        JsonValue valor when valor.TryGetValue<int>(out var inteiro) => inteiro,
        JsonValue valor when valor.TryGetValue<string>(out var texto) &&
                             int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lido) => lido,
        _ => throw new ArgumentException($"{nome} deve ser um número inteiro.")
    };

    private static bool Booleano(JsonObject argumentos, string nome) =>
        argumentos[nome] is JsonValue valor && valor.TryGetValue<bool>(out var booleano) && booleano;

    private static string? Opcao(string[] args, string nome)
    {
        var indice = Array.IndexOf(args, nome);
        return indice >= 0 && indice + 1 < args.Length ? args[indice + 1] : null;
    }

    private static JsonObject Conteudo(string texto, bool erro) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = texto }),
        ["isError"] = erro
    };

    private static JsonObject Resultado(JsonNode id, JsonObject resultado) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = resultado };

    private static JsonObject Erro(JsonNode? id, int codigo, string mensagem) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = codigo, ["message"] = mensagem }
    };

    private static JsonArray Ferramentas()
    {
        var planilha = Propriedade("string", "Alias da planilha cadastrada (ou seu ID/URL).");
        var celula = new JsonObject { ["type"] = new JsonArray("string", "number", "boolean", "null") };
        var matriz = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = celula } };
        return
        [
            Ferramenta("listar_planilhas", "Estado da conta conectada e planilhas cadastradas, com descrições e regiões anotadas.",
                [], [], leitura: true),
            Ferramenta("descrever_planilha", "Título, localidade, abas (grade, área usada a partir de A1, mesclagens) e regiões anotadas.",
                [("planilha", planilha)], ["planilha"], leitura: true),
            Ferramenta("ler_intervalo",
                "Lê um intervalo A1 com a aba (Aba!A1:H20, 'Aba com espaço'!B3) ou o nome de uma região anotada. Devolve só " +
                "células com conteúdo: endereço, valor exibido, bruto quando difere, fórmula e mesclagem. Prefira regiões pequenas.",
                [("planilha", planilha), ("intervalo", Propriedade("string", "Intervalo A1 com aba, nome da aba ou região anotada.")),
                 ("limite_de_celulas", Propriedade("integer", "Máximo de células lidas (padrão 500, máximo 5000)."))],
                ["planilha", "intervalo"], leitura: true),
            Ferramenta("buscar_na_planilha",
                "Procura um texto nos valores exibidos (sem diferenciar maiúsculas/acentos), em todas as abas ou na aba/intervalo " +
                "informado. Devolve coordenadas e as demais células da mesma linha; exatas primeiro.",
                [("planilha", planilha), ("texto", Propriedade("string", "Texto procurado.")),
                 ("aba", Propriedade("string", "Restringe a uma aba.")), ("intervalo", Propriedade("string", "Restringe a um intervalo A1.")),
                 ("limite", Propriedade("integer", "Máximo de ocorrências (padrão 20, máximo 100)."))],
                ["planilha", "texto"], leitura: true),
            Ferramenta("atualizar_celulas",
                "Escreve valores em retângulos exatos (até 200 células), tudo ou nada. Texto é interpretado como digitado na " +
                "localidade da planilha (\"129,90\" em pt_BR vira número; \"=A1*2\" vira fórmula); números e booleanos JSON são " +
                "gravados como tais; null/\"\" limpa a célula. Informe valores_esperados com o valor exibido que você leu: " +
                "divergência recusa a escrita. Células com fórmula exigem permitir_sobrescrever_formulas, após confirmar com o usuário.",
                [("planilha", planilha),
                 ("alteracoes", new JsonObject
                 {
                     ["type"] = "array",
                     ["items"] = new JsonObject
                     {
                         ["type"] = "object",
                         ["properties"] = new JsonObject
                         {
                             ["intervalo"] = Propriedade("string", "Retângulo A1 com aba, ex. Gastos!D12 ou 'Aba 1'!B3:C3."),
                             ["valores"] = matriz.DeepClone(),
                             ["valores_esperados"] = matriz.DeepClone()
                         },
                         ["required"] = new JsonArray("intervalo", "valores")
                     }
                 }),
                 ("permitir_sobrescrever_formulas", Propriedade("boolean", "Só após confirmação explícita do usuário."))],
                ["planilha", "alteracoes"], leitura: false),
            Ferramenta("atualizar_por_referencia",
                "Localiza uma célula de referência pelo texto e escreve um valor na mesma linha (coluna informada ou deslocamento). " +
                "Se o texto corresponder a mais de uma célula, nada é escrito e os candidatos voltam para você confirmar com o usuário.",
                [("planilha", planilha), ("referencia", Propriedade("string", "Texto da célula de referência.")),
                 ("aba", Propriedade("string", "Restringe a busca a uma aba.")),
                 ("intervalo", Propriedade("string", "Restringe a busca a um intervalo A1 com aba.")),
                 ("coluna", Propriedade("string", "Letra da coluna do alvo, ex. D.")),
                 ("deslocamento_de_colunas", Propriedade("integer", "Colunas à direita (negativo: à esquerda) da referência.")),
                 ("deslocamento_de_linhas", Propriedade("integer", "Linhas abaixo (negativo: acima) da referência.")),
                 ("valor", celula.DeepClone().AsObject()), ("valor_esperado", celula.DeepClone().AsObject()),
                 ("permitir_sobrescrever_formulas", Propriedade("boolean", "Só após confirmação explícita do usuário."))],
                ["planilha", "referencia", "valor"], leitura: false),
            Ferramenta("adicionar_linha",
                "Acrescenta uma linha logo após a tabela contida no intervalo (ex. Gastos!A1:D1), sem inserir linhas na grade.",
                [("planilha", planilha), ("intervalo_da_tabela", Propriedade("string", "Intervalo A1 com aba que contém a tabela.")),
                 ("valores", new JsonObject { ["type"] = "array", ["items"] = celula.DeepClone() })],
                ["planilha", "intervalo_da_tabela", "valores"], leitura: false),
            Ferramenta("cadastrar_planilha", "Cadastra uma planilha por URL ou Spreadsheet ID com um alias e descrição opcional.",
                [("alias", Propriedade("string", "Alias curto, ex. financas.")), ("url_ou_id", Propriedade("string", "URL ou ID.")),
                 ("descricao", Propriedade("string", "Para que serve a planilha."))],
                ["alias", "url_ou_id"], leitura: false),
            Ferramenta("anotar_regiao",
                "Anota no cadastro local uma região recorrente (nome, intervalo A1 com aba, descrição) para consultas futuras. " +
                "Não altera a planilha.",
                [("planilha", planilha), ("nome", Propriedade("string", "Nome da região.")),
                 ("intervalo", Propriedade("string", "Intervalo A1 com aba.")), ("descricao", Propriedade("string", "O que há nela."))],
                ["planilha", "nome", "intervalo"], leitura: false)
        ];
    }

    private static JsonObject Propriedade(string tipo, string descricao) => new() { ["type"] = tipo, ["description"] = descricao };

    private static JsonObject Ferramenta(string nome, string descricao, (string Nome, JsonObject Schema)[] propriedades,
        string[] obrigatorias, bool leitura)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(propriedades
            .Select(p => KeyValuePair.Create(p.Nome, p.Schema.DeepClone()))) };
        if (obrigatorias.Length > 0) schema["required"] = new JsonArray(obrigatorias.Select(o => (JsonNode)o).ToArray());
        return new JsonObject
        {
            ["name"] = nome,
            ["description"] = descricao,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = leitura,
                ["destructiveHint"] = nome is "atualizar_celulas" or "atualizar_por_referencia",
                ["openWorldHint"] = true
            }
        };
    }
}
