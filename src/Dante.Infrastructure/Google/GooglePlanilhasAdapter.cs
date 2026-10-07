using System.Text.RegularExpressions;
using Dante.Application.Planilhas;

namespace Dante.Infrastructure.Google;

// Seleção técnica por MIME: as ferramentas e a Application continuam genéricas.
internal sealed class GooglePlanilhasAdapter(GoogleOAuthService autenticacao, GoogleSheetsAdapter sheets,
    GoogleDriveXlsxAdapter drive) : IPlanilhaService
{
    private const string MimeSheets = "application/vnd.google-apps.spreadsheet";
    private static readonly Regex UrlDrive = new(@"^https://drive\.google\.com/file/d/([A-Za-z0-9_-]+)(?:/|\?|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string? IdentificarPlanilha(string urlOuId)
    {
        var match = UrlDrive.Match(urlOuId.Trim());
        return sheets.IdentificarPlanilha(match.Success ? match.Groups[1].Value : urlOuId);
    }

    private async Task<GoogleDriveXlsxAdapter.Arquivo?> XlsxAsync(string id, CancellationToken ct)
    {
        if (!autenticacao.TemPermissaoDeDrive) return null;
        var arquivo = await drive.ObterArquivoAsync(id, ct);
        if (arquivo.Mime == GoogleDriveXlsxAdapter.MimeXlsx) return arquivo;
        if (arquivo.Mime == MimeSheets) return null;
        throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoSuportada,
            "Este arquivo não é uma planilha Google Sheets nem um XLSX suportado.");
    }

    public async Task<PlanilhaDto> ObterMetadadosAsync(string idDaPlanilha, bool calcularAreaUsada, CancellationToken cancellationToken = default) =>
        await XlsxAsync(idDaPlanilha, cancellationToken) is { } arquivo
            ? await drive.ObterMetadadosAsync(arquivo, calcularAreaUsada, cancellationToken)
            : await sheets.ObterMetadadosAsync(idDaPlanilha, calcularAreaUsada, cancellationToken);

    public async Task<IntervaloDaPlanilhaDto> LerAsync(string idDaPlanilha, IntervaloA1 intervalo, int limiteDeCelulas,
        CancellationToken cancellationToken = default) =>
        await XlsxAsync(idDaPlanilha, cancellationToken) is { } arquivo
            ? await drive.LerAsync(arquivo, intervalo, limiteDeCelulas, cancellationToken)
            : await sheets.LerAsync(idDaPlanilha, intervalo, limiteDeCelulas, cancellationToken);

    public async Task<IReadOnlyList<ValoresLidos>> LerValoresExibidosAsync(string idDaPlanilha, IReadOnlyList<IntervaloA1> intervalos,
        CancellationToken cancellationToken = default) =>
        await XlsxAsync(idDaPlanilha, cancellationToken) is { } arquivo
            ? await drive.LerValoresAsync(arquivo, intervalos, cancellationToken)
            : await sheets.LerValoresExibidosAsync(idDaPlanilha, intervalos, cancellationToken);

    public async Task<IReadOnlyList<string>> AtualizarAsync(string idDaPlanilha, IReadOnlyList<ValoresParaEscrita> escritas,
        CancellationToken cancellationToken = default) =>
        await XlsxAsync(idDaPlanilha, cancellationToken) is { } arquivo
            ? await drive.AtualizarAsync(arquivo, escritas, cancellationToken)
            : await sheets.AtualizarAsync(idDaPlanilha, escritas, cancellationToken);

    public async Task<IntervaloA1> AdicionarLinhaAsync(string idDaPlanilha, IntervaloA1 tabela, IReadOnlyList<ValorDeCelula> valores,
        CancellationToken cancellationToken = default) =>
        await XlsxAsync(idDaPlanilha, cancellationToken) is { } arquivo
            ? await drive.AdicionarLinhaAsync(arquivo, tabela, valores, cancellationToken)
            : await sheets.AdicionarLinhaAsync(idDaPlanilha, tabela, valores, cancellationToken);
}
