using Microsoft.Extensions.Configuration;

namespace Dante.Infrastructure.Google;

// Cliente OAuth do tipo "app para computador" criado pelo usuário no Google Cloud (#224). Google__ClientId e
// Google__ClientSecret vêm do ambiente do serviço, nunca do repositório; ao conectar, são guardados junto da credencial
// cifrada para que o servidor MCP das sessões renove tokens sem herdar o ambiente do Worker.
public sealed record GoogleOptions
{
    public const string Escopos = "openid email https://www.googleapis.com/auth/spreadsheets";
    public const string EscopoDePlanilhas = "https://www.googleapis.com/auth/spreadsheets";
    public const string EscopoDeDrive = "https://www.googleapis.com/auth/drive";

    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string DiretorioDaCredencial { get; init; } = Padrao("google");
    public byte[]? ChaveDaCredencial { get; init; }
    public int PortaDoCallback { get; init; }
    public bool PermitirXlsxNoDrive { get; init; }
    internal string EscoposSolicitados => PermitirXlsxNoDrive ? Escopos + " " + EscopoDeDrive : Escopos;
    public TimeSpan TempoParaAutorizar { get; init; } = TimeSpan.FromMinutes(5);

    public bool Configurada => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    // DANTE_GOOGLE_DIR troca ~/.dante/google; DANTE_GOOGLE_KEY (base64 de 32 bytes) substitui o arquivo de chave.
    public static GoogleOptions DaConfiguracao(IConfiguration configuration) => new()
    {
        ClientId = configuration["Google:ClientId"],
        ClientSecret = configuration["Google:ClientSecret"],
        DiretorioDaCredencial = configuration["DANTE_GOOGLE_DIR"] is { Length: > 0 } dir ? Path.GetFullPath(dir) : Padrao("google"),
        ChaveDaCredencial = configuration["DANTE_GOOGLE_KEY"] is { Length: > 0 } chave ? Convert.FromBase64String(chave) : null,
        PortaDoCallback = int.TryParse(configuration["Google:CallbackPort"], out var porta) ? porta : 0,
        PermitirXlsxNoDrive = bool.TryParse(configuration["Google:PermitirXlsxNoDrive"], out var xlsx) && xlsx
    };

    internal static string Padrao(string nome) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", nome);
}
