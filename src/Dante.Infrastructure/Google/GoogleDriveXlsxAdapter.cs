using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Dante.Application.Planilhas;
using Dante.Infrastructure.Planilhas;

namespace Dante.Infrastructure.Google;

// A edição Office da interface web não faz parte da API Sheets. Este adapter trabalha sobre o arquivo
// binário no Drive, pelo ID já cadastrado, sem conversão ou descoberta de arquivos.
internal sealed class GoogleDriveXlsxAdapter(GoogleOAuthService autenticacao, HttpClient http,
    Func<TimeSpan, CancellationToken, Task>? esperar = null)
{
    internal const string MimeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    // Drive v2 conserva Files.etag; v3 retirou esse campo. Metadados e upload usam a mesma versão
    // para o If-Match verificar a revisão do recurso correto.
    private const string Base = "https://www.googleapis.com/drive/v2/files/";
    private readonly Func<TimeSpan, CancellationToken, Task> esperar = esperar ?? Task.Delay;

    internal sealed record Arquivo(string Id, string Nome, string Mime, string Revisao, string? ETag, bool PodeEditar,
        bool PodeBaixar, long Tamanho);

    internal async Task<Arquivo> ObterArquivoAsync(string id, CancellationToken ct)
    {
        using var resposta = await EnviarAsync(HttpMethod.Get, Base + Uri.EscapeDataString(id) +
            "?supportsAllDrives=true&fields=id,title,mimeType,version,etag,fileSize,capabilities(canEdit,canDownload)", null, null, ct);
        var json = JsonNode.Parse(await resposta.Content.ReadAsStringAsync(ct))?.AsObject()
                   ?? throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Indisponivel, "O Drive devolveu metadados inválidos.");
        var capacidades = json["capabilities"];
        return new(id, (string?)json["title"] ?? "", (string?)json["mimeType"] ?? "", json["version"]?.ToString() ?? "",
            (string?)json["etag"], (bool?)capacidades?["canEdit"] == true,
            (bool?)capacidades?["canDownload"] == true,
            long.TryParse(json["fileSize"]?.ToString(), out var tamanho) ? tamanho : 0);
    }

    internal async Task<PlanilhaDto> ObterMetadadosAsync(Arquivo arquivo, bool area, CancellationToken ct) =>
        (await BaixarAsync(arquivo, ct)).Metadados(arquivo.Id, arquivo.Nome, area);

    internal async Task<IntervaloDaPlanilhaDto> LerAsync(Arquivo arquivo, IntervaloA1 intervalo, int limite, CancellationToken ct) =>
        (await BaixarAsync(arquivo, ct)).Ler(intervalo, limite, arquivo.Revisao);

    internal async Task<IReadOnlyList<ValoresLidos>> LerValoresAsync(Arquivo arquivo, IReadOnlyList<IntervaloA1> intervalos,
        CancellationToken ct) => (await BaixarAsync(arquivo, ct)).LerValores(intervalos);

    internal async Task<IReadOnlyList<string>> AtualizarAsync(Arquivo arquivo, IReadOnlyList<ValoresParaEscrita> escritas,
        CancellationToken ct)
    {
        ExigirEscrita(arquivo);
        if (escritas.Any(e => e.RevisaoEsperada is null || e.RevisaoEsperada != arquivo.Revisao)) throw Conflito();
        var documento = await BaixarAsync(arquivo, ct);
        documento.Atualizar(escritas);
        await SalvarAsync(arquivo, documento.Salvar(), ct);
        return escritas.Select(e => e.Intervalo.ToString()).ToArray();
    }

    internal async Task<IntervaloA1> AdicionarLinhaAsync(Arquivo arquivo, IntervaloA1 tabela,
        IReadOnlyList<ValorDeCelula> valores, CancellationToken ct)
    {
        ExigirEscrita(arquivo);
        var documento = await BaixarAsync(arquivo, ct);
        var intervalo = documento.AdicionarLinha(tabela, valores);
        await SalvarAsync(arquivo, documento.Salvar(), ct);
        return intervalo;
    }

    private async Task<DocumentoXlsx> BaixarAsync(Arquivo arquivo, CancellationToken ct)
    {
        if (arquivo.Mime != MimeXlsx)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoSuportada, "O arquivo não é uma planilha XLSX.");
        if (!arquivo.PodeBaixar)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.SemPermissao, "A conta conectada não pode baixar este XLSX.");
        if (arquivo.Tamanho > DocumentoXlsx.MaximoDeBytes)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoSuportada, "O XLSX excede o limite de 25 MB.");
        using var resposta = await EnviarAsync(HttpMethod.Get, Base + Uri.EscapeDataString(arquivo.Id) +
            "?alt=media&supportsAllDrives=true", null, null, ct);
        await using var origem = await resposta.Content.ReadAsStreamAsync(ct);
        using var destino = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int lidos;
        while ((lidos = await origem.ReadAsync(buffer, ct)) > 0)
        {
            if (destino.Length + lidos > DocumentoXlsx.MaximoDeBytes)
                throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoSuportada, "O XLSX excede o limite de 25 MB.");
            destino.Write(buffer, 0, lidos);
        }
        // Não associar os bytes baixados à revisão errada se o editor mudou o arquivo durante o download.
        var atual = await ObterArquivoAsync(arquivo.Id, ct);
        if (atual.Revisao != arquivo.Revisao || atual.Mime != arquivo.Mime) throw Conflito();
        return new DocumentoXlsx(destino.ToArray());
    }

    private async Task SalvarAsync(Arquivo arquivo, byte[] conteudo, CancellationToken ct)
    {
        var atual = await ObterArquivoAsync(arquivo.Id, ct);
        if (atual.Revisao != arquivo.Revisao || atual.ETag != arquivo.ETag || atual.Mime != arquivo.Mime) throw Conflito();
        ExigirEscrita(atual);
        // A verificação acima cobre download/edição; If-Match cobre a janela entre a verificação e o PUT.
        using var resposta = await EnviarAsync(HttpMethod.Put,
            "https://www.googleapis.com/upload/drive/v2/files/" + Uri.EscapeDataString(arquivo.Id) +
            "?uploadType=media&convert=false&newRevision=true&supportsAllDrives=true&fields=id,version", conteudo, arquivo.ETag, ct);
    }

    private static void ExigirEscrita(Arquivo arquivo)
    {
        if (!arquivo.PodeEditar)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.SemPermissao, "A conta conectada não pode editar este XLSX.");
        if (string.IsNullOrEmpty(arquivo.Revisao) || string.IsNullOrEmpty(arquivo.ETag) || arquivo.ETag.StartsWith("W/", StringComparison.Ordinal))
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoSuportada,
                "O Drive não forneceu uma revisão e ETag fortes para proteger esta escrita contra alterações concorrentes. Nada foi alterado.");
    }

    private async Task<HttpResponseMessage> EnviarAsync(HttpMethod metodo, string url, byte[]? conteudo, string? etag,
        CancellationToken ct)
    {
        var renovado = false;
        var renovarAgora = false;
        for (var tentativa = 0; ; tentativa++)
        {
            using var pedido = new HttpRequestMessage(metodo, url);
            pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await autenticacao.ObterTokenDeAcessoAsync(renovarAgora, ct));
            renovarAgora = false;
            if (etag is not null) pedido.Headers.IfMatch.ParseAdd(etag);
            if (conteudo is not null)
            {
                pedido.Content = new ByteArrayContent(conteudo);
                pedido.Content.Headers.ContentType = new MediaTypeHeaderValue(MimeXlsx);
            }
            HttpResponseMessage resposta;
            try { resposta = await http.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Indisponivel, conteudo is null
                    ? "Não foi possível falar com o Drive agora; verifique a rede."
                    : "A confirmação da escrita no Drive não chegou. Confira o arquivo antes de repetir a alteração.", innerException: e);
            }
            if (resposta.IsSuccessStatusCode) return resposta;
            var status = resposta.StatusCode;
            var espera = resposta.Headers.RetryAfter?.Delta;
            resposta.Dispose();
            if (status == HttpStatusCode.Unauthorized && !renovado) { renovado = true; renovarAgora = true; continue; }
            // Uploads não são repetidos: uma falha pode chegar depois de o Drive ter gravado o conteúdo.
            if (conteudo is null && tentativa < 3 && (status == HttpStatusCode.TooManyRequests || (int)status >= 500))
            {
                await esperar(TimeSpan.FromSeconds(Math.Clamp(espera?.TotalSeconds ?? Math.Pow(2, tentativa), 1, 30)), ct);
                continue;
            }
            throw status switch
            {
                HttpStatusCode.PreconditionFailed => Conflito(),
                HttpStatusCode.Unauthorized => new(MotivoDaFalhaDePlanilha.NaoConectada, "Reconecte a conta com /google connect."),
                HttpStatusCode.Forbidden => new(MotivoDaFalhaDePlanilha.SemPermissao,
                    "O Drive recusou o acesso. Habilite a API do Drive e reconecte com acesso a XLSX; confira a permissão do arquivo."),
                HttpStatusCode.NotFound => new(MotivoDaFalhaDePlanilha.NaoEncontrada, "Arquivo não encontrado para a conta conectada."),
                HttpStatusCode.TooManyRequests => new(MotivoDaFalhaDePlanilha.LimiteDeTaxa, "Limite de requisições do Drive; tente mais tarde."),
                _ => new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Indisponivel, conteudo is null
                    ? "O Drive recusou o pedido; tente novamente em instantes."
                    : "O Drive não confirmou a escrita. Confira o arquivo antes de repetir a alteração.")
            };
        }
    }

    private static FalhaDePlanilhaException Conflito() => new(MotivoDaFalhaDePlanilha.Conflito,
        "O XLSX mudou desde a leitura. Nada foi sobrescrito; leia novamente e confirme os valores antes de repetir.");
}
