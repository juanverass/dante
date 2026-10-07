using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dante.Application.Planilhas;

namespace Dante.Infrastructure.Google;

// Adapter genérico do Google Sheets API v4 por HTTP (#224, AD-55), sem SDK: traduz a porta IPlanilhaService para
// spreadsheets.get (metadados, gridData com exibido/bruto/fórmula e mesclagens), values:batchGet, values:batchUpdate
// e values:append. Não conhece o significado dos dados nem formato de nenhuma planilha. 401 renova o token uma vez;
// 429 e 5xx são repetidos com espera; os demais erros viram FalhaDePlanilhaException com mensagem segura.
public sealed class GoogleSheetsAdapter(GoogleOAuthService autenticacao, HttpClient http,
    Func<TimeSpan, CancellationToken, Task>? esperar = null) : IPlanilhaService
{
    internal const string Base = "https://sheets.googleapis.com/v4/spreadsheets/";
    private const int Tentativas = 4;
    private static readonly Regex UrlDePlanilha =
        new(@"^https?://docs\.google\.com/spreadsheets/(?:u/\d+/)?d/([A-Za-z0-9_-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IdDePlanilha = new("^[A-Za-z0-9_-]{25,100}$", RegexOptions.Compiled);
    private readonly Func<TimeSpan, CancellationToken, Task> esperar = esperar ?? Task.Delay;

    // URL de edição/compartilhamento (…/spreadsheets/d/<id>/…) ou o ID puro.
    public string? IdentificarPlanilha(string urlOuId)
    {
        var texto = urlOuId.Trim();
        if (UrlDePlanilha.Match(texto) is { Success: true } url) texto = url.Groups[1].Value;
        return IdDePlanilha.IsMatch(texto) ? texto : null;
    }

    public async Task<PlanilhaDto> ObterMetadadosAsync(string idDaPlanilha, bool calcularAreaUsada,
        CancellationToken cancellationToken = default)
    {
        var corpo = await EnviarAsync(HttpMethod.Get, Id(idDaPlanilha) + "?fields=" + Uri.EscapeDataString(
            "spreadsheetId,spreadsheetUrl,properties(title,locale,timeZone)," +
            "sheets(properties(sheetId,title,index,sheetType,gridProperties(rowCount,columnCount)),merges)"), null, cancellationToken);
        var abas = (corpo["sheets"] as JsonArray ?? []).OfType<JsonObject>().Select(Aba).ToList();
        if (calcularAreaUsada)
        {
            var grades = abas.Where(a => a.Tipo == "GRID").ToArray();
            var lidos = grades.Length == 0 ? [] :
                await LerValoresExibidosAsync(idDaPlanilha, grades.Select(a => IntervaloA1.DaAba(a.Titulo)).ToArray(), cancellationToken);
            for (var i = 0; i < grades.Length && i < lidos.Count; i++)
            {
                var linhas = lidos[i].Linhas;
                var colunas = linhas.Count == 0 ? 0 : linhas.Max(l => l.Count);
                var indice = abas.IndexOf(grades[i]);
                abas[indice] = grades[i] with
                {
                    AreaUsada = linhas.Count == 0 || colunas == 0 ? null : IntervaloA1.Retangulo(null, 1, 1, linhas.Count, colunas).Celulas
                };
            }
        }
        var propriedades = corpo["properties"] as JsonObject;
        return new PlanilhaDto
        {
            IdDaPlanilha = Texto(corpo, "spreadsheetId") ?? idDaPlanilha,
            Titulo = Texto(propriedades, "title") ?? string.Empty,
            Localidade = Texto(propriedades, "locale"),
            FusoHorario = Texto(propriedades, "timeZone"),
            Url = Texto(corpo, "spreadsheetUrl"),
            Abas = abas
        };
    }

    public async Task<IntervaloDaPlanilhaDto> LerAsync(string idDaPlanilha, IntervaloA1 intervalo, int limiteDeCelulas,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limiteDeCelulas, 1);
        var alvo = intervalo;
        if (intervalo.AbaInteira)
        {
            // A aba inteira é reduzida à área com dados antes de pedir a grade completa.
            var lidos = (await LerValoresExibidosAsync(idDaPlanilha, [intervalo], cancellationToken)).FirstOrDefault();
            var colunas = lidos is null || lidos.Linhas.Count == 0 ? 0 : lidos.Linhas.Max(l => l.Count);
            if (colunas == 0)
                return new IntervaloDaPlanilhaDto { Aba = intervalo.Aba!, Intervalo = intervalo.ToString() };
            alvo = IntervaloA1.Retangulo(intervalo.Aba, 1, 1, lidos!.Linhas.Count, colunas);
        }
        var truncado = alvo.QuantidadeDeCelulas > limiteDeCelulas;
        if (truncado)
        {
            var colunas = Math.Min(alvo.QuantidadeDeColunas, limiteDeCelulas);
            var linhas = Math.Max(1, limiteDeCelulas / colunas);
            alvo = IntervaloA1.Retangulo(alvo.Aba, alvo.Linha, alvo.Coluna, alvo.Linha + linhas - 1, alvo.Coluna + colunas - 1);
        }

        var corpo = await EnviarAsync(HttpMethod.Get, Id(idDaPlanilha) + "?includeGridData=true&ranges=" +
            Uri.EscapeDataString(alvo.ToString()) + "&fields=" + Uri.EscapeDataString(
                "sheets(properties(title),merges,data(startRow,startColumn,rowData(values(formattedValue,effectiveValue,userEnteredValue))))"),
            null, cancellationToken);
        var aba = (corpo["sheets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var titulo = Texto(aba?["properties"] as JsonObject, "title") ?? alvo.Aba ?? string.Empty;
        var mesclagens = Mesclagens(aba).Where(m => m.Intersecta(alvo)).ToArray();
        var ancoras = mesclagens.ToDictionary(m => (m.Linha, m.Coluna), m => m.Celulas);
        var celulas = new List<CelulaDaPlanilhaDto>();
        foreach (var dados in (aba?["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var linhaInicial = Inteiro(dados, "startRow") + 1;
            var colunaInicial = Inteiro(dados, "startColumn") + 1;
            var linhas = dados["rowData"] as JsonArray ?? [];
            for (var i = 0; i < linhas.Count; i++)
            {
                var valores = (linhas[i] as JsonObject)?["values"] as JsonArray ?? [];
                for (var j = 0; j < valores.Count; j++)
                {
                    if (valores[j] is not JsonObject valor) continue;
                    var (linha, coluna) = (linhaInicial + i, colunaInicial + j);
                    var celula = new CelulaDaPlanilhaDto
                    {
                        Endereco = IntervaloA1.Endereco(linha, coluna),
                        Linha = linha,
                        Coluna = coluna,
                        ValorExibido = Texto(valor, "formattedValue"),
                        ValorBruto = Bruto(valor["effectiveValue"] as JsonObject),
                        Formula = Texto(valor["userEnteredValue"] as JsonObject, "formulaValue"),
                        Mesclagem = ancoras.GetValueOrDefault((linha, coluna))
                    };
                    if (!celula.EstaVazia || celula.Mesclagem is not null) celulas.Add(celula);
                }
            }
        }
        return new IntervaloDaPlanilhaDto
        {
            Aba = titulo,
            Intervalo = alvo.NaAba(titulo).ToString(),
            Celulas = celulas.OrderBy(c => c.Linha).ThenBy(c => c.Coluna).ToArray(),
            Mesclagens = mesclagens.Select(m => m.Celulas).ToArray(),
            Truncado = truncado
        };
    }

    public async Task<IReadOnlyList<ValoresLidos>> LerValoresExibidosAsync(string idDaPlanilha,
        IReadOnlyList<IntervaloA1> intervalos, CancellationToken cancellationToken = default)
    {
        if (intervalos.Count == 0) return [];
        var consulta = string.Join('&', intervalos.Select(i => "ranges=" + Uri.EscapeDataString(i.ToString())));
        var corpo = await EnviarAsync(HttpMethod.Get, Id(idDaPlanilha) + "/values:batchGet?" + consulta +
            "&valueRenderOption=FORMATTED_VALUE&majorDimension=ROWS", null, cancellationToken);
        var faixas = (corpo["valueRanges"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        return faixas.Select((faixa, i) =>
        {
            var pedido = intervalos[Math.Min(i, intervalos.Count - 1)];
            var devolvido = Texto(faixa, "range") is { } texto && IntervaloA1.TentarInterpretar(texto, out var lido) ? lido! : null;
            var origem = devolvido is { AbaInteira: false }
                ? IntervaloA1.DaCelula(devolvido.Aba ?? pedido.Aba, devolvido.Linha, devolvido.Coluna)
                : IntervaloA1.DaCelula(pedido.Aba, pedido.AbaInteira ? 1 : pedido.Linha, pedido.AbaInteira ? 1 : pedido.Coluna);
            var linhas = (faixa["values"] as JsonArray ?? []).Select(linha =>
                (IReadOnlyList<string>)(linha as JsonArray ?? []).Select(v => v?.ToString() ?? string.Empty).ToArray()).ToArray();
            return new ValoresLidos(origem, linhas);
        }).ToArray();
    }

    public async Task<IReadOnlyList<string>> AtualizarAsync(string idDaPlanilha, IReadOnlyList<ValoresParaEscrita> escritas,
        CancellationToken cancellationToken = default)
    {
        var dados = new JsonArray(escritas.Select(escrita => (JsonNode)new JsonObject
        {
            ["range"] = escrita.Intervalo.ToString(),
            ["majorDimension"] = "ROWS",
            ["values"] = new JsonArray(escrita.Valores.Select(linha => (JsonNode)new JsonArray(linha.Select(Json).ToArray())).ToArray())
        }).ToArray());
        var corpo = await EnviarAsync(HttpMethod.Post, Id(idDaPlanilha) + "/values:batchUpdate", new JsonObject
        {
            ["valueInputOption"] = "USER_ENTERED",
            ["includeValuesInResponse"] = false,
            ["data"] = dados
        }, cancellationToken);
        var atualizados = (corpo["responses"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => Texto(r, "updatedRange")).OfType<string>().ToArray();
        return atualizados.Length > 0 ? atualizados : escritas.Select(e => e.Intervalo.ToString()).ToArray();
    }

    public async Task<IntervaloA1> AdicionarLinhaAsync(string idDaPlanilha, IntervaloA1 tabela,
        IReadOnlyList<ValorDeCelula> valores, CancellationToken cancellationToken = default)
    {
        // OVERWRITE escreve nas células vazias logo após a tabela, sem inserir linhas na grade.
        var corpo = await EnviarAsync(HttpMethod.Post, Id(idDaPlanilha) + "/values/" + Uri.EscapeDataString(tabela.ToString()) +
            ":append?valueInputOption=USER_ENTERED&insertDataOption=OVERWRITE&includeValuesInResponse=false", new JsonObject
        {
            ["range"] = tabela.ToString(),
            ["majorDimension"] = "ROWS",
            ["values"] = new JsonArray(new JsonArray(valores.Select(Json).ToArray()))
        }, cancellationToken);
        var atualizado = Texto(corpo["updates"] as JsonObject, "updatedRange");
        return atualizado is not null && IntervaloA1.TentarInterpretar(atualizado, out var intervalo) && !intervalo!.AbaInteira
            ? intervalo
            : throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Indisponivel,
                "O Google não informou onde a linha foi escrita; confira a planilha antes de repetir.");
    }

    private async Task<JsonObject> EnviarAsync(HttpMethod metodo, string caminho, JsonObject? conteudo,
        CancellationToken cancellationToken)
    {
        var renovado = false;
        for (var tentativa = 1; ; tentativa++)
        {
            var token = await autenticacao.ObterTokenDeAcessoAsync(renovado && tentativa > 1, cancellationToken);
            using var requisicao = new HttpRequestMessage(metodo, Base + caminho);
            requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (conteudo is not null)
                requisicao.Content = new StringContent(conteudo.ToJsonString(), Encoding.UTF8, "application/json");
            HttpResponseMessage resposta;
            try { resposta = await http.SendAsync(requisicao, cancellationToken); }
            catch (Exception exception) when (exception is HttpRequestException ||
                                              exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (tentativa < Tentativas)
                {
                    await esperar(Espera(tentativa, null), cancellationToken);
                    continue;
                }
                throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Indisponivel,
                    "Não foi possível falar com o Google Sheets agora; verifique a rede.", innerException: exception);
            }
            using (resposta)
            {
                var texto = await resposta.Content.ReadAsStringAsync(cancellationToken);
                if (resposta.IsSuccessStatusCode)
                    return (string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto) as JsonObject) ?? [];
                var mensagem = MensagemDoGoogle(texto);
                switch (resposta.StatusCode)
                {
                    case HttpStatusCode.Unauthorized when !renovado:
                        renovado = true;
                        continue;
                    case HttpStatusCode.Unauthorized:
                        throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoConectada,
                            "O Google recusou o acesso da conta conectada; reconecte com /google connect.");
                    case HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout when tentativa < Tentativas:
                        await esperar(Espera(tentativa, resposta.Headers.RetryAfter), cancellationToken);
                        continue;
                    case HttpStatusCode.TooManyRequests:
                        throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.LimiteDeTaxa,
                            "Limite de requisições do Google Sheets atingido; tente novamente em um minuto.");
                    case HttpStatusCode.Forbidden:
                        throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.SemPermissao,
                            "Sem permissão nesta planilha para a conta conectada" + Detalhe(mensagem));
                    case HttpStatusCode.NotFound:
                        throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoEncontrada,
                            "Planilha não encontrada para a conta conectada" + Detalhe(mensagem));
                    case HttpStatusCode.BadRequest:
                        if (mensagem?.Contains("not supported for this document", StringComparison.OrdinalIgnoreCase) == true)
                            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoSuportada,
                                "Este documento não é uma planilha nativa do Google Sheets. Para XLSX no Drive, habilite " +
                                "Google__PermitirXlsxNoDrive=true e a API do Drive e reconecte com /google connect.");
                        throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Invalida,
                            "O Google Sheets recusou o pedido" + Detalhe(mensagem));
                    default:
                        throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Indisponivel,
                            $"O Google Sheets respondeu {(int)resposta.StatusCode}; tente novamente em instantes.");
                }
            }
        }
    }

    private static TimeSpan Espera(int tentativa, RetryConditionHeaderValue? retryAfter)
    {
        var sugerida = retryAfter?.Delta;
        var padrao = TimeSpan.FromSeconds(Math.Pow(2, tentativa - 1));
        return sugerida is { } delta && delta > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(delta.TotalSeconds, 30)) : padrao;
    }

    // A mensagem de erro do Google descreve o pedido (intervalo, permissão), nunca o token.
    private static string? MensagemDoGoogle(string texto)
    {
        try
        {
            var mensagem = Texto((JsonNode.Parse(texto) as JsonObject)?["error"] as JsonObject, "message");
            return mensagem is null ? null : mensagem.Length > 300 ? mensagem[..300] : mensagem;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Detalhe(string? mensagem) => mensagem is null ? "." : $": {mensagem}";

    private static AbaDaPlanilhaDto Aba(JsonObject aba)
    {
        var propriedades = aba["properties"] as JsonObject;
        var grade = propriedades?["gridProperties"] as JsonObject;
        return new AbaDaPlanilhaDto
        {
            IdDaAba = propriedades?["sheetId"] is JsonValue id && id.TryGetValue<long>(out var valor) ? valor : 0,
            Titulo = Texto(propriedades, "title") ?? string.Empty,
            Indice = Inteiro(propriedades, "index"),
            Tipo = Texto(propriedades, "sheetType") ?? "GRID",
            Linhas = Inteiro(grade, "rowCount"),
            Colunas = Inteiro(grade, "columnCount"),
            Mesclagens = Mesclagens(aba).Select(m => m.Celulas).ToArray()
        };
    }

    // GridRange do Google: índices a partir de 0 e fim exclusivo.
    private static IEnumerable<IntervaloA1> Mesclagens(JsonObject? aba) =>
        (aba?["merges"] as JsonArray ?? []).OfType<JsonObject>()
        .Where(m => Inteiro(m, "endRowIndex") > Inteiro(m, "startRowIndex") && Inteiro(m, "endColumnIndex") > Inteiro(m, "startColumnIndex"))
        .Select(m => IntervaloA1.Retangulo(null, Inteiro(m, "startRowIndex") + 1, Inteiro(m, "startColumnIndex") + 1,
            Inteiro(m, "endRowIndex"), Inteiro(m, "endColumnIndex")));

    private static ValorDeCelula? Bruto(JsonObject? efetivo)
    {
        if (efetivo is null) return null;
        if (efetivo["numberValue"] is JsonValue numero && numero.TryGetValue<double>(out var n)) return ValorDeCelula.DeNumero(n);
        if (efetivo["boolValue"] is JsonValue booleano && booleano.TryGetValue<bool>(out var b)) return ValorDeCelula.DeBooleano(b);
        if (Texto(efetivo, "stringValue") is { } texto) return ValorDeCelula.DeTexto(texto);
        if (efetivo["errorValue"] is JsonObject erro) return ValorDeCelula.DeErro(Texto(erro, "type") ?? "ERROR");
        return null;
    }

    private static JsonNode? Json(ValorDeCelula? valor) => valor?.Tipo switch
    {
        TipoDeValorDaCelula.Numero => JsonValue.Create(valor.Numero!.Value),
        TipoDeValorDaCelula.Booleano => JsonValue.Create(valor.Booleano!.Value),
        TipoDeValorDaCelula.Texto or TipoDeValorDaCelula.Erro => JsonValue.Create(valor.Texto ?? string.Empty),
        // String vazia limpa o valor da célula sem tocar a formatação.
        _ => JsonValue.Create(string.Empty)
    };

    private static string Id(string idDaPlanilha) => Uri.EscapeDataString(idDaPlanilha);

    private static string? Texto(JsonObject? objeto, string nome) =>
        objeto?[nome] is JsonValue valor && valor.TryGetValue<string>(out var texto) ? texto : null;

    private static int Inteiro(JsonObject? objeto, string nome) =>
        objeto?[nome] is JsonValue valor && valor.TryGetValue<int>(out var inteiro) ? inteiro : 0;
}
