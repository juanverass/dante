using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dante.Application;
using Dante.Infrastructure;
using Dante.Worker;
using Dante.Worker.Agents;
using Dante.Worker.Repositories;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
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

    private static readonly Assembly[] Nucleo = [typeof(AgentKind).Assembly, typeof(AgentContextResolver).Assembly];

    // #166: o núcleo compilado não alcança hosts, adapters nem providers, nem mesmo por referência indireta.
    [Fact]
    public void CoreAssembliesDoNotReferenceHostsAdaptersOrProviders()
    {
        Assert.Equal(["Dante.Application", "Dante.Domain"], Nucleo.Select(a => a.GetName().Name).Order());
        foreach (var assembly in Nucleo)
            Assert.DoesNotContain(assembly.GetReferencedAssemblies().Select(a => a.Name!), nome =>
                nome is "Dante.Worker" or "Dante.Infrastructure" or "Dante.WebApi" ||
                nome.StartsWith("Telegram", StringComparison.OrdinalIgnoreCase) ||
                nome.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) ||
                nome.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CoreContractsDoNotIntroduceTId()
    {
        var tipos = Nucleo.SelectMany(a => a.GetTypes()).ToArray();
        var parametros = tipos.Where(t => t.IsGenericTypeDefinition).SelectMany(t => t.GetGenericArguments())
            .Concat(tipos.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(m => m.IsGenericMethodDefinition).SelectMany(m => m.GetGenericArguments()));
        Assert.DoesNotContain(parametros, parametro => parametro.Name == "TId");
    }

    // O núcleo não faz IO de filesystem, processos, rede ou banco; manipular texto de caminho (Path) é permitido.
    [Fact]
    public void CoreSourcesDoNotPerformFilesystemProcessOrNetworkAccess()
    {
        var proibido = new Regex(@"\b(File|Directory|FileInfo|DirectoryInfo|FileStream|Process|HttpClient|NpgsqlConnection)\b\s*[.(]");
        var violacoes = new[] { "Dante.Domain", "Dante.Application" }
            .SelectMany(projeto => Directory.EnumerateFiles(Path.Combine(Raiz(), "src", projeto), "*.cs",
                SearchOption.AllDirectories))
            .Where(arquivo => !arquivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(arquivo => File.ReadLines(arquivo).Where(linha => proibido.IsMatch(linha))
                .Select(linha => $"{Path.GetFileName(arquivo)}: {linha.Trim()}"));
        Assert.Empty(violacoes);
    }

    // Review do PR #175: o modelo do Domain não carrega caminho de execução, GitHub, variável do host nem
    // ambiente/segredos; esses contratos operacionais vivem na Application (saída do caso de uso) ou nos adapters.
    [Fact]
    public void DomainModelDoesNotCarryOperationalOrHostConcepts()
    {
        var operacional = new Regex("Path|Directory|Diretorio|Caminho|Workspace|GitHub|Host|Environment|Ambiente|Secret|Segredo",
            RegexOptions.IgnoreCase);
        const BindingFlags membros = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly;
        var nomes = typeof(AgentKind).Assembly.GetTypes().SelectMany(tipo => tipo.GetProperties(membros)
            .Select(propriedade => $"{tipo.Name}.{propriedade.Name}")
            .Concat(tipo.GetFields(membros).Select(campo => $"{tipo.Name}.{campo.Name}")).Prepend(tipo.Name));
        Assert.DoesNotContain(nomes, nome => operacional.IsMatch(nome));
        Assert.Equal("Dante.Application", typeof(JobExecutionContext).Assembly.GetName().Name);
        Assert.Equal("Dante.Application", typeof(ResolvedRepositoryEnvironment).Assembly.GetName().Name);
        Assert.Equal("Dante.Worker", typeof(RepositoryDefinition).Assembly.GetName().Name);
    }

    [Fact]
    public void WorkerAdaptersImplementTheApplicationContextPorts()
    {
        Assert.True(typeof(IWorkspaceGeral).IsAssignableFrom(typeof(GeneralWorkspace)));
        Assert.True(typeof(ICatalogoDeRepositorios).IsAssignableFrom(typeof(RepositoryRegistry)));
        Assert.True(typeof(IPreferenciasDoAssistente).IsAssignableFrom(typeof(AssistantSettingsStore)));
        Assert.All(new[] { typeof(IWorkspaceGeral), typeof(ICatalogoDeRepositorios), typeof(IPreferenciasDoAssistente) },
            porta => Assert.Equal("Dante.Application", porta.Assembly.GetName().Name));
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
