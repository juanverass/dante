using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dante.Application.Planilhas;

namespace Dante.Tests;

// Emulador em memória do Google Sheets API v4 e dos endpoints OAuth (#224), no formato das respostas reais usadas pelo
// adapter: spreadsheets.get (metadados e gridData), values:batchGet, values:batchUpdate (USER_ENTERED em pt_BR) e
// values:append. Registra cada requisição para os testes conferirem o que saiu, sem rede.
internal sealed class GoogleSheetsFalso : HttpMessageHandler
{
    public const string IdPadrao = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcdEFG";
    public const string RefreshToken = "rt-segredo-de-teste-123";
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public sealed class Celula
    {
        public string? Exibido { get; set; }
        public double? Numero { get; set; }
        public bool? Booleano { get; set; }
        public string? Texto { get; set; }
        public string? Formula { get; set; }
    }

    public sealed class Aba(long id, string titulo, int linhas, int colunas)
    {
        public long Id { get; } = id;
        public string Titulo { get; } = titulo;
        public int Linhas { get; } = linhas;
        public int Colunas { get; } = colunas;
        public Dictionary<(int Linha, int Coluna), Celula> Celulas { get; } = [];
        public List<IntervaloA1> Mesclagens { get; } = [];
    }

    public sealed record Requisicao(HttpMethod Metodo, string Url, string? Corpo, string? Token);

    public string IdDaPlanilha { get; set; } = IdPadrao;
    public string Titulo { get; set; } = "Planilha de teste";
    public List<Aba> Abas { get; } = [];
    public List<Requisicao> Requisicoes { get; } = [];
    public Queue<HttpStatusCode> FalhasDaApi { get; } = new();
    public string? TokenRecusado { get; set; }
    public bool FalharDepoisDoAppend { get; set; }
    public HttpStatusCode StatusDeRevogacao { get; set; } = HttpStatusCode.OK;
    public bool RefreshInvalido { get; set; }
    public bool SemRefreshToken { get; set; }
    public string Escopo { get; set; } = "openid https://www.googleapis.com/auth/spreadsheets email";
    public int Renovacoes { get; private set; }
    public int TokensEmitidos { get; private set; }
    public Dictionary<string, string>? UltimoFormularioDeCodigo { get; private set; }
    public List<string> Revogados { get; } = [];

    public IEnumerable<Requisicao> Escritas => Requisicoes.Where(r => r.Metodo == HttpMethod.Post && r.Url.Contains("sheets.googleapis.com"));

    public Aba AdicionarAba(string titulo, int linhas = 1000, int colunas = 26)
    {
        var aba = new Aba(Abas.Count == 0 ? 0 : 1000 + Abas.Count, titulo, linhas, colunas);
        Abas.Add(aba);
        return aba;
    }

    public void Definir(string aba, string endereco, object? valor, string? formula = null, string? exibido = null)
    {
        var alvo = Abas.Single(a => a.Titulo == aba);
        var intervalo = IntervaloA1.Interpretar(endereco);
        var celula = valor switch
        {
            null => new Celula(),
            string texto => new Celula { Texto = texto, Exibido = texto },
            bool booleano => new Celula { Booleano = booleano, Exibido = booleano ? "TRUE" : "FALSE" },
            _ => new Celula { Numero = Convert.ToDouble(valor, CultureInfo.InvariantCulture),
                Exibido = Convert.ToDouble(valor, CultureInfo.InvariantCulture).ToString(PtBr) }
        };
        celula.Formula = formula;
        if (exibido is not null) celula.Exibido = exibido;
        alvo.Celulas[(intervalo.Linha, intervalo.Coluna)] = celula;
    }

    public void Mesclar(string aba, string intervalo) => Abas.Single(a => a.Titulo == aba).Mesclagens.Add(IntervaloA1.Interpretar(intervalo));

    public string? Exibido(string aba, string endereco)
    {
        var intervalo = IntervaloA1.Interpretar(endereco);
        return Abas.Single(a => a.Titulo == aba).Celulas.GetValueOrDefault((intervalo.Linha, intervalo.Coluna))?.Exibido;
    }

    public Celula? Obter(string aba, string endereco)
    {
        var intervalo = IntervaloA1.Interpretar(endereco);
        return Abas.Single(a => a.Titulo == aba).Celulas.GetValueOrDefault((intervalo.Linha, intervalo.Coluna));
    }

    private HttpResponseMessage RespostaDoAppend(string intervalo, JsonObject corpo)
    {
        var resultado = Acrescentar(intervalo, corpo);
        if (FalharDepoisDoAppend) throw new HttpRequestException("Resposta perdida após aplicar append.");
        return Json(HttpStatusCode.OK, resultado);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var corpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var token = request.Headers.Authorization?.Parameter;
        Requisicoes.Add(new Requisicao(request.Method, request.RequestUri!.ToString(), corpo, token));
        var uri = request.RequestUri!;
        if (uri.Host == "oauth2.googleapis.com") return Token(uri.AbsolutePath, Formulario(corpo));
        if (uri.Host != "sheets.googleapis.com") return Json(HttpStatusCode.NotFound, new JsonObject());
        if (FalhasDaApi.TryDequeue(out var falha))
            return Json(falha, new JsonObject { ["error"] = new JsonObject { ["code"] = (int)falha, ["message"] = "falha simulada" } });
        if (token is null || token == TokenRecusado)
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = new JsonObject { ["message"] = "Request had invalid authentication credentials." } });

        var caminho = Uri.UnescapeDataString(uri.AbsolutePath["/v4/spreadsheets/".Length..]);
        var consulta = Consulta(uri.Query);
        var id = caminho.Split('/', ':')[0];
        if (id != IdDaPlanilha)
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["message"] = "Requested entity was not found." } });
        var resto = caminho[id.Length..];
        try
        {
            return (request.Method.Method, resto) switch
            {
                ("GET", "") when consulta.Contains(("includeGridData", "true")) => Json(HttpStatusCode.OK, Grade(Valor(consulta, "ranges"))),
                ("GET", "") => Json(HttpStatusCode.OK, Metadados()),
                ("GET", "/values:batchGet") => Json(HttpStatusCode.OK, LoteDeValores(consulta.Where(c => c.Nome == "ranges").Select(c => c.Valor))),
                ("POST", "/values:batchUpdate") => Json(HttpStatusCode.OK, Atualizar(JsonNode.Parse(corpo!)!.AsObject())),
                ("POST", _) when resto.StartsWith("/values/", StringComparison.Ordinal) && resto.EndsWith(":append", StringComparison.Ordinal) =>
                    RespostaDoAppend(resto["/values/".Length..^":append".Length], JsonNode.Parse(corpo!)!.AsObject()),
                _ => Json(HttpStatusCode.NotFound, new JsonObject())
            };
        }
        catch (ArgumentException exception)
        {
            return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = new JsonObject { ["message"] = "Unable to parse range: " + exception.Message } });
        }
    }

    private HttpResponseMessage Token(string caminho, Dictionary<string, string> formulario)
    {
        if (caminho == "/revoke")
        {
            Revogados.Add(formulario["token"]);
            return Json(StatusDeRevogacao, new JsonObject());
        }
        if (formulario.GetValueOrDefault("grant_type") == "refresh_token")
        {
            if (RefreshInvalido || formulario.GetValueOrDefault("refresh_token") != RefreshToken)
                return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "Token has been expired or revoked." });
            Renovacoes++;
            return Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = $"at-{++TokensEmitidos}", ["expires_in"] = 3599, ["token_type"] = "Bearer" });
        }
        UltimoFormularioDeCodigo = formulario;
        if (formulario.GetValueOrDefault("code") != "codigo-valido")
            return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" });
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"pessoa@example.com\",\"sub\":\"1\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var resposta = new JsonObject
        {
            ["access_token"] = $"at-{++TokensEmitidos}", ["expires_in"] = 3599, ["scope"] = Escopo, ["token_type"] = "Bearer",
            ["id_token"] = $"e30.{payload}.assinatura"
        };
        if (!SemRefreshToken) resposta["refresh_token"] = RefreshToken;
        return Json(HttpStatusCode.OK, resposta);
    }

    private JsonObject Metadados() => new()
    {
        ["spreadsheetId"] = IdDaPlanilha,
        ["spreadsheetUrl"] = $"https://docs.google.com/spreadsheets/d/{IdDaPlanilha}/edit",
        ["properties"] = new JsonObject { ["title"] = Titulo, ["locale"] = "pt_BR", ["timeZone"] = "America/Sao_Paulo" },
        ["sheets"] = new JsonArray(Abas.Select((aba, i) => (JsonNode)new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["sheetId"] = aba.Id, ["title"] = aba.Titulo, ["index"] = i, ["sheetType"] = "GRID",
                ["gridProperties"] = new JsonObject { ["rowCount"] = aba.Linhas, ["columnCount"] = aba.Colunas }
            },
            ["merges"] = Mesclagens(aba)
        }).ToArray())
    };

    private JsonObject Grade(string intervaloTexto)
    {
        var intervalo = IntervaloA1.Interpretar(intervaloTexto);
        var aba = AbaDe(intervalo);
        var (linhaFinal, colunaFinal) = intervalo.AbaInteira ? (aba.Linhas, aba.Colunas) : (intervalo.LinhaFinal, intervalo.ColunaFinal);
        var ocupadas = aba.Celulas.Keys.Where(k => k.Linha >= intervalo.Linha && k.Linha <= linhaFinal && k.Coluna >= intervalo.Coluna && k.Coluna <= colunaFinal).ToArray();
        var ultimaLinha = ocupadas.Length == 0 ? intervalo.Linha - 1 : ocupadas.Max(k => k.Linha);
        var linhas = new JsonArray();
        for (var l = intervalo.Linha; l <= ultimaLinha; l++)
        {
            var naLinha = ocupadas.Where(k => k.Linha == l).ToArray();
            var ultimaColuna = naLinha.Length == 0 ? intervalo.Coluna - 1 : naLinha.Max(k => k.Coluna);
            var valores = new JsonArray();
            for (var c = intervalo.Coluna; c <= ultimaColuna; c++)
                valores.Add(aba.Celulas.TryGetValue((l, c), out var celula) ? CelulaJson(celula) : new JsonObject());
            linhas.Add(naLinha.Length == 0 ? new JsonObject() : new JsonObject { ["values"] = valores });
        }
        return new JsonObject
        {
            ["sheets"] = new JsonArray(new JsonObject
            {
                ["properties"] = new JsonObject { ["title"] = aba.Titulo },
                ["merges"] = Mesclagens(aba),
                ["data"] = new JsonArray(new JsonObject
                {
                    ["startRow"] = intervalo.Linha - 1, ["startColumn"] = intervalo.Coluna - 1, ["rowData"] = linhas
                })
            })
        };
    }

    private JsonObject LoteDeValores(IEnumerable<string> intervalos) => new()
    {
        ["spreadsheetId"] = IdDaPlanilha,
        ["valueRanges"] = new JsonArray(intervalos.Select(texto =>
        {
            var intervalo = IntervaloA1.Interpretar(texto);
            var aba = AbaDe(intervalo);
            var (linha, coluna) = intervalo.AbaInteira ? (1, 1) : (intervalo.Linha, intervalo.Coluna);
            var (linhaFinal, colunaFinal) = intervalo.AbaInteira ? (aba.Linhas, aba.Colunas) : (intervalo.LinhaFinal, intervalo.ColunaFinal);
            var ocupadas = aba.Celulas.Where(p => !string.IsNullOrEmpty(p.Value.Exibido) && p.Key.Linha >= linha && p.Key.Linha <= linhaFinal &&
                                                  p.Key.Coluna >= coluna && p.Key.Coluna <= colunaFinal).Select(p => p.Key).ToArray();
            var faixa = new JsonObject
            {
                ["range"] = $"{Citada(aba.Titulo)}!{IntervaloA1.Retangulo(null, linha, coluna, linhaFinal, colunaFinal).Celulas}",
                ["majorDimension"] = "ROWS"
            };
            if (ocupadas.Length == 0) return (JsonNode)faixa;
            var valores = new JsonArray();
            for (var l = linha; l <= ocupadas.Max(k => k.Linha); l++)
            {
                var naLinha = ocupadas.Where(k => k.Linha == l).ToArray();
                var linhaJson = new JsonArray();
                if (naLinha.Length > 0)
                    for (var c = coluna; c <= naLinha.Max(k => k.Coluna); c++)
                        linhaJson.Add(aba.Celulas.GetValueOrDefault((l, c))?.Exibido ?? "");
                valores.Add(linhaJson);
            }
            faixa["values"] = valores;
            return (JsonNode)faixa;
        }).ToArray())
    };

    private JsonObject Atualizar(JsonObject corpo)
    {
        if ((string?)corpo["valueInputOption"] != "USER_ENTERED") throw new ArgumentException("valueInputOption");
        var respostas = new JsonArray();
        foreach (var dado in corpo["data"]!.AsArray().OfType<JsonObject>())
        {
            var intervalo = IntervaloA1.Interpretar((string)dado["range"]!);
            var aba = AbaDe(intervalo);
            var linhas = dado["values"]!.AsArray();
            for (var i = 0; i < linhas.Count; i++)
            {
                var valores = linhas[i]!.AsArray();
                for (var j = 0; j < valores.Count; j++) Escrever(aba, intervalo.Linha + i, intervalo.Coluna + j, valores[j]);
            }
            respostas.Add(new JsonObject { ["updatedRange"] = $"{Citada(aba.Titulo)}!{intervalo.Celulas}" });
        }
        return new JsonObject { ["spreadsheetId"] = IdDaPlanilha, ["responses"] = respostas };
    }

    private JsonObject Acrescentar(string intervaloTexto, JsonObject corpo)
    {
        var tabela = IntervaloA1.Interpretar(intervaloTexto);
        var aba = AbaDe(tabela);
        // Como no Google: a tabela começa na primeira linha do intervalo e segue enquanto houver dado nas colunas dele.
        var linha = tabela.Linha;
        while (aba.Celulas.Any(p => p.Key.Linha == linha && p.Key.Coluna >= tabela.Coluna && p.Key.Coluna <= tabela.ColunaFinal &&
                                    !string.IsNullOrEmpty(p.Value.Exibido))) linha++;
        var valores = corpo["values"]![0]!.AsArray();
        for (var j = 0; j < valores.Count; j++) Escrever(aba, linha, tabela.Coluna + j, valores[j]);
        var escrito = IntervaloA1.Retangulo(null, linha, tabela.Coluna, linha, tabela.Coluna + valores.Count - 1);
        return new JsonObject { ["updates"] = new JsonObject { ["updatedRange"] = $"{Citada(aba.Titulo)}!{escrito.Celulas}" } };
    }

    private static void Escrever(Aba aba, int linha, int coluna, JsonNode? valor)
    {
        if (valor is JsonValue numero && numero.GetValueKind() == JsonValueKind.Number)
        {
            aba.Celulas[(linha, coluna)] = new Celula { Numero = numero.GetValue<double>(), Exibido = numero.GetValue<double>().ToString(PtBr) };
            return;
        }
        if (valor is JsonValue booleano && booleano.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            aba.Celulas[(linha, coluna)] = new Celula { Booleano = booleano.GetValue<bool>(), Exibido = booleano.GetValue<bool>() ? "TRUE" : "FALSE" };
            return;
        }
        var texto = valor?.GetValue<string>() ?? "";
        if (texto.Length == 0) aba.Celulas.Remove((linha, coluna));
        else if (texto.StartsWith('=')) aba.Celulas[(linha, coluna)] = new Celula { Formula = texto, Exibido = "0", Numero = 0 };
        else if (Regex.IsMatch(texto, @"^-?\d+(,\d+)?$"))
            aba.Celulas[(linha, coluna)] = new Celula { Numero = double.Parse(texto, PtBr), Exibido = texto };
        else aba.Celulas[(linha, coluna)] = new Celula { Texto = texto, Exibido = texto };
    }

    private Aba AbaDe(IntervaloA1 intervalo) => intervalo.Aba is null ? Abas[0] :
        Abas.SingleOrDefault(a => a.Titulo == intervalo.Aba) ?? throw new ArgumentException(intervalo.ToString());

    private static JsonObject CelulaJson(Celula celula)
    {
        var json = new JsonObject();
        if (celula.Exibido is not null) json["formattedValue"] = celula.Exibido;
        if (celula.Numero is { } numero) json["effectiveValue"] = new JsonObject { ["numberValue"] = numero };
        else if (celula.Booleano is { } booleano) json["effectiveValue"] = new JsonObject { ["boolValue"] = booleano };
        else if (celula.Texto is { } texto) json["effectiveValue"] = new JsonObject { ["stringValue"] = texto };
        json["userEnteredValue"] = celula.Formula is { } formula ? new JsonObject { ["formulaValue"] = formula } :
            celula.Numero is { } n ? new JsonObject { ["numberValue"] = n } : new JsonObject { ["stringValue"] = celula.Texto ?? "" };
        return json;
    }

    private static JsonArray Mesclagens(Aba aba) => new(aba.Mesclagens.Select(m => (JsonNode)new JsonObject
    {
        ["sheetId"] = aba.Id, ["startRowIndex"] = m.Linha - 1, ["endRowIndex"] = m.LinhaFinal,
        ["startColumnIndex"] = m.Coluna - 1, ["endColumnIndex"] = m.ColunaFinal
    }).ToArray());

    private static string Citada(string titulo) => Regex.IsMatch(titulo, "^[A-Za-z0-9_]+$") ? titulo : "'" + titulo.Replace("'", "''") + "'";

    private static List<(string Nome, string Valor)> Consulta(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(par => par.Split('=', 2))
        .Select(par => (Uri.UnescapeDataString(par[0]), par.Length > 1 ? Uri.UnescapeDataString(par[1]) : ""))
        .ToList();

    private static string Valor(List<(string Nome, string Valor)> consulta, string nome) => consulta.First(c => c.Nome == nome).Valor;

    private static Dictionary<string, string> Formulario(string? corpo) => (corpo ?? "")
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(par => par.Split('=', 2))
        .ToDictionary(par => Uri.UnescapeDataString(par[0].Replace('+', ' ')),
            par => par.Length > 1 ? Uri.UnescapeDataString(par[1].Replace('+', ' ')) : "");

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject corpo) => new(status)
    {
        Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json")
    };
}
