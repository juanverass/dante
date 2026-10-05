using System.Xml.Linq;
using Dante.Application;
using Dante.Infrastructure;
using Dante.Worker;
using Dante.Worker.Agents;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dante.Tests;

public sealed class HexagonalArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string[]> Permitidas = new Dictionary<string, string[]>
    {
        ["Dante.Domain"] = [],
        ["Dante.Application"] = ["Dante.Domain"],
        ["Dante.Infrastructure"] = ["Dante.Application", "Dante.Domain"],
        ["Dante.Worker"] = ["Dante.Application", "Dante.Infrastructure"],
        ["Dante.WebApi"] = ["Dante.Application", "Dante.Infrastructure"]
    };

    [Fact]
    public void SolutionContainsAllLayersAndTheirReferencesPointInward()
    {
        var raiz = Raiz();
        var solution = File.ReadAllText(Path.Combine(raiz, "Dante.sln"));
        foreach (var (projeto, referencias) in Permitidas)
        {
            Assert.Contains(projeto + ".csproj", solution);
            var arquivo = Path.Combine(raiz, "src", projeto, projeto + ".csproj");
            var xml = XDocument.Load(arquivo);
            Assert.Empty(Violacoes(projeto, xml));
            Assert.Equal("net10.0", xml.Descendants("TargetFramework").Single().Value);
            var atuais = xml.Descendants("ProjectReference").Select(r => Path.GetFileNameWithoutExtension(
                r.Attribute("Include")!.Value.Replace('\\', '/'))).Order();
            Assert.Equal(referencias.Order(), atuais);
        }
    }

    [Theory]
    [InlineData("Dante.Domain", "Dante.Application")]
    [InlineData("Dante.Application", "Dante.Infrastructure")]
    [InlineData("Dante.Infrastructure", "Dante.Worker")]
    [InlineData("Dante.Worker", "Dante.WebApi")]
    [InlineData("Dante.WebApi", "Dante.Worker")]
    [InlineData("Dante.WebApi", "Dante.Domain")]
    public void ArchitectureGuardRejectsForbiddenProjectReferences(string origem, string destino)
    {
        var xml = XDocument.Parse($"<Project><ItemGroup><ProjectReference Include='../{destino}/{destino}.csproj'/></ItemGroup></Project>");
        Assert.NotEmpty(Violacoes(origem, xml));
    }

    [Theory]
    [InlineData("Dante.Domain", "Mapster")]
    [InlineData("Dante.Domain", "Mapster.DependencyInjection")]
    [InlineData("Dante.Domain", "Microsoft.EntityFrameworkCore")]
    [InlineData("Dante.Domain", "Npgsql")]
    [InlineData("Dante.Domain", "Telegram.Bot")]
    [InlineData("Dante.Domain", "Microsoft.AspNetCore.Http.Abstractions")]
    [InlineData("Dante.Application", "Npgsql")]
    [InlineData("Dante.Application", "Microsoft.EntityFrameworkCore")]
    [InlineData("Dante.Application", "Telegram.Bot")]
    [InlineData("Dante.Application", "Microsoft.AspNetCore.Http.Abstractions")]
    public void ArchitectureGuardRejectsProviderDependenciesInTheCore(string camada, string pacote)
    {
        var xml = XDocument.Parse($"<Project><ItemGroup><PackageReference Include='{pacote}' Version='1.0'/></ItemGroup></Project>");
        Assert.NotEmpty(Violacoes(camada, xml));
    }

    [Theory]
    [InlineData("Dante.Domain", "System.Collections.Immutable")]
    [InlineData("Dante.Application", "System.Collections.Immutable")]
    [InlineData("Dante.Application", "Microsoft.Extensions.DependencyInjection.Abstractions")]
    [InlineData("Dante.Application", "Mapster")]
    public void ArchitectureGuardAllowsSupportedDependenciesInTheCore(string camada, string pacote)
    {
        var xml = XDocument.Parse($"<Project><ItemGroup><PackageReference Include='{pacote}' Version='1.0'/></ItemGroup></Project>");
        Assert.Empty(Violacoes(camada, xml));
    }

    [Fact]
    public void HostsShareCompositionWhileWebCompositionDoesNotRegisterTelegramOrLegacyWorkers()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        Assert.Same(services, services.AddApplication().AddInfrastructure(configuration));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(ITelegramBotApi) || s.ServiceType == typeof(IHostedService));
        services.AddLogging();
        services.AddWorker(configuration);
        Assert.Contains(services, s => s.ServiceType == typeof(IAgentProcessExecutor));
        Assert.Contains(services, s => s.ServiceType == typeof(SessionRegistry));
        Assert.Contains(services, s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(Dante.Worker.Worker));
        Assert.Contains(services, s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(TelegramPollingService));
    }

    private static IEnumerable<string> Violacoes(string projeto, XDocument xml)
    {
        foreach (var referencia in xml.Descendants("ProjectReference"))
        {
            var destino = Path.GetFileNameWithoutExtension(referencia.Attribute("Include")!.Value.Replace('\\', '/'));
            if (!Permitidas[projeto].Contains(destino)) yield return $"{projeto} → {destino}";
        }
        foreach (var referencia in xml.Descendants("Reference"))
        {
            var destino = referencia.Attribute("Include")!.Value.Split(',')[0];
            if (destino.StartsWith("Dante.") && !Permitidas[projeto].Contains(destino)) yield return $"{projeto} → {destino}";
        }
        foreach (var pacote in xml.Descendants("PackageReference").Select(x => x.Attribute("Include")!.Value))
            if ((projeto == "Dante.Domain" || projeto == "Dante.Application") &&
                EhProviderProibido(projeto, pacote))
                yield return $"Provider no núcleo: {pacote}";
    }

    private static bool EhProviderProibido(string projeto, string pacote) =>
        pacote.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
        pacote.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) ||
        pacote.StartsWith("Telegram.", StringComparison.OrdinalIgnoreCase) ||
        pacote.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) ||
        (projeto == "Dante.Domain" && pacote.StartsWith("Mapster", StringComparison.OrdinalIgnoreCase));

    private static string Raiz()
    {
        for (var diretorio = new DirectoryInfo(AppContext.BaseDirectory); diretorio is not null; diretorio = diretorio.Parent)
            if (File.Exists(Path.Combine(diretorio.FullName, "Dante.sln"))) return diretorio.FullName;
        throw new DirectoryNotFoundException("Solution não encontrada.");
    }
}
