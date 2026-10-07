using Dante.Application.Planilhas;
using Dante.Infrastructure.Google;
using Dante.Infrastructure.Planilhas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure.Composicao;

internal static class PlanilhasServiceCollectionExtensions
{
    // Google Sheets é o primeiro provider da porta genérica (#224). Sem Google__ClientId/ClientSecret os adapters
    // existem e respondem "não configurada"; nada é contatado na inicialização.
    internal static void AddPlanilhas(this IServiceCollection services, IConfiguration configuration)
    {
        var opcoes = GoogleOptions.DaConfiguracao(configuration);
        var diretorio = configuration["DANTE_PLANILHAS_DIR"] is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : GoogleOptions.Padrao("planilhas");
        services.AddSingleton(opcoes);
        services.AddSingleton(_ => new GoogleCredentialStore(opcoes.DiretorioDaCredencial, opcoes.ChaveDaCredencial));
        services.AddSingleton(provider => new GoogleOAuthService(opcoes, provider.GetRequiredService<GoogleCredentialStore>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
        services.AddSingleton<IConexaoDePlanilha>(provider => provider.GetRequiredService<GoogleOAuthService>());
        services.AddSingleton<IPlanilhaService>(provider =>
        {
            var autenticacao = provider.GetRequiredService<GoogleOAuthService>();
            return new GooglePlanilhasAdapter(autenticacao, new GoogleSheetsAdapter(autenticacao, autenticacao.Http),
                new GoogleDriveXlsxAdapter(autenticacao, autenticacao.Http));
        });
        services.AddSingleton<ICadastroDePlanilhas>(_ => new CadastroDePlanilhasEmArquivo(diretorio));
        services.AddSingleton<IAuditoriaDePlanilhas>(_ => new AuditoriaDePlanilhasEmArquivo(diretorio));
    }
}
