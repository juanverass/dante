using Dante.Application;
using Dante.Application.Planilhas;
using Dante.Infrastructure.Google;
using Dante.Infrastructure.Planilhas;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// Composição de teste da capacidade de planilhas (#224): Application real, adapters reais da Infrastructure (OAuth,
// Sheets, cadastro e auditoria em diretório temporário) e o GoogleSheetsFalso no lugar da rede.
internal sealed class AmbienteDePlanilhas : IDisposable
{
    private readonly IServiceScope scope;

    public AmbienteDePlanilhas(bool conectado = true, GoogleOptions? opcoes = null)
    {
        Diretorio = Directory.CreateTempSubdirectory("dante-planilhas-").FullName;
        Opcoes = opcoes ?? new GoogleOptions
        {
            ClientId = "cliente-de-teste.apps.googleusercontent.com", ClientSecret = "segredo-do-cliente",
            DiretorioDaCredencial = Path.Combine(Diretorio, "google")
        };
        Store = new GoogleCredentialStore(Opcoes.DiretorioDaCredencial);
        if (conectado)
            Store.Salvar(new CredencialGoogle(Opcoes.ClientId ?? "c", Opcoes.ClientSecret ?? "s", GoogleSheetsFalso.RefreshToken,
                "pessoa@example.com", DateTimeOffset.UnixEpoch, GoogleOptions.Escopos));
        Http = new HttpClient(Google);
        OAuth = new GoogleOAuthService(Opcoes, Store, Http, Relogio);
        Adapter = new GoogleSheetsAdapter(OAuth, Http, (espera, _) => { Esperas.Add(espera); return Task.CompletedTask; });
        Cadastro = new CadastroDePlanilhasEmArquivo(Path.Combine(Diretorio, "planilhas"));
        Auditoria = new AuditoriaDePlanilhasEmArquivo(Path.Combine(Diretorio, "planilhas"));
        Provider = new ServiceCollection().AddApplication()
            .AddSingleton<IConexaoDePlanilha>(OAuth).AddSingleton<IPlanilhaService>(Adapter)
            .AddSingleton<ICadastroDePlanilhas>(Cadastro).AddSingleton<IAuditoriaDePlanilhas>(Auditoria)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        scope = Provider.CreateScope();
    }

    public GoogleSheetsFalso Google { get; } = new();
    public RelogioDeTeste Relogio { get; } = new();
    public List<TimeSpan> Esperas { get; } = [];
    public string Diretorio { get; }
    public GoogleOptions Opcoes { get; }
    public GoogleCredentialStore Store { get; }
    public HttpClient Http { get; }
    public GoogleOAuthService OAuth { get; }
    public GoogleSheetsAdapter Adapter { get; }
    public CadastroDePlanilhasEmArquivo Cadastro { get; }
    public AuditoriaDePlanilhasEmArquivo Auditoria { get; }
    public ServiceProvider Provider { get; }
    public PlanilhasAppService Servico => scope.ServiceProvider.GetRequiredService<PlanilhasAppService>();

    public Task CadastrarAsync(string alias = "financas", string? descricao = null) => Cadastro.SalvarAsync(new PlanilhaCadastradaDto
    {
        Alias = alias, IdDaPlanilha = Google.IdDaPlanilha, Titulo = Google.Titulo, Descricao = descricao, CadastradaEm = DateTimeOffset.UnixEpoch
    });

    public string[] LinhasDeAuditoria() => File.Exists(Auditoria.Caminho) ? File.ReadAllLines(Auditoria.Caminho) : [];

    public void Dispose()
    {
        scope.Dispose();
        Provider.Dispose();
        OAuth.Dispose();
        Http.Dispose();
        try { Directory.Delete(Diretorio, recursive: true); }
        catch (IOException) { }
    }
}

internal sealed class RelogioDeTeste : TimeProvider
{
    public DateTimeOffset Agora { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Agora;
}
