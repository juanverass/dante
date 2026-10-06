using System.Text.RegularExpressions;
using Dante.Application;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Infrastructure;
using Dante.Infrastructure.Data;
using Dante.Worker;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #209: regras de organização e fronteira da Application (Epic #202) executáveis. Complementa
// HexagonalArchitectureTests (referências entre projetos) e MapeamentoTests (mappings por feature).
// Os guards percorrem GetTypes() completo: um tipo aninhado não contorna a regra (review do PR #222).
public sealed class ApplicationArchitectureTests
{
    private static readonly System.Reflection.Assembly Application = typeof(AutorizacaoDoBrain).Assembly;
    private static readonly System.Reflection.Assembly Domain = typeof(AgentKind).Assembly;
    private static readonly System.Reflection.Assembly Infrastructure = typeof(DanteDbContext).Assembly;

    // Legado extraído do Worker (AD-36, AD-38) e pastas técnicas da própria Application, fora da convenção feature-first.
    private static readonly string[] ForaDasFeatures = ["Agentes", "Anexos", "Contextos", "Uso", "Comum", "Mapeamento", "Composicao"];
    private static readonly string[] Subpastas = ["Contratos", "Portas", "CasosDeUso", "Validacao"];

    [Fact]
    public void ApplicationReferenciaSomenteDomainEAbstracoesSuportadas()
    {
        var externas = Application.GetReferencedAssemblies().Select(a => a.Name!)
            .Where(nome => nome != "netstandard" && !nome.StartsWith("System", StringComparison.Ordinal)).Order();
        Assert.Equal(["Dante.Domain", "Mapster", "Microsoft.Extensions.DependencyInjection.Abstractions"], externas);
    }

    [Fact]
    public void DomainNaoTemMapperValidatorPortaNemProviderTecnico()
    {
        Assert.All(Domain.GetReferencedAssemblies().Select(a => a.Name!), nome =>
            Assert.True(nome == "netstandard" || nome.StartsWith("System", StringComparison.Ordinal), nome));
        var tipos = Domain.GetTypes();
        Assert.DoesNotContain(tipos, t => t.IsInterface);
        Assert.DoesNotContain(tipos, t => Regex.IsMatch(t.Name, "(Mapping|Mapper|Validator|Repository|AppService|Dto|SearchDto|DbContext)$"));
    }

    // Portas ficam na Application; Infrastructure só as implementa. Repository específico implementa a porta da
    // feature homônima; as únicas interfaces próprias da Infrastructure são detalhes técnicos da execução de agentes.
    [Fact]
    public void PortasFicamNaApplicationEInfrastructureSoAsImplementa()
    {
        Assert.Equal(["IAgentExecutableResolver", "IAgentProcessExecutor", "IInteractiveAgentProcessLauncher"],
            Infrastructure.GetTypes().Where(t => t.IsInterface).Select(t => t.Name).Order());

        var repositories = Infrastructure.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } &&
            t.Name.EndsWith("Repository", StringComparison.Ordinal) && t.Namespace!.StartsWith("Dante.Infrastructure.Modulos.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, repositories.Length);
        Assert.All(repositories, adapter =>
        {
            var porta = Assert.Single(adapter.GetInterfaces(), i => i.Name == "I" + adapter.Name);
            Assert.Same(Application, porta.Assembly);
            Assert.Equal("Dante.Application." + adapter.Namespace!.Split('.')[^1], porta.Namespace);
        });

        var services = new ServiceCollection().AddInfrastructure(ConfiguracaoComBanco());
        var contratos = services.Select(s => s.ServiceType).Where(t => t.IsInterface && t.Assembly.GetName().Name!.StartsWith("Dante.", StringComparison.Ordinal));
        Assert.All(contratos, contrato => Assert.True(contrato.Assembly == Application || contrato.Assembly == Infrastructure, contrato.Name));

        // Toda porta da Application (interface que AddApplication não implementa) tem adapter registrado pela Infrastructure.
        var proprios = new ServiceCollection().AddApplication().Select(s => s.ServiceType).ToHashSet();
        var portas = Application.GetTypes().Where(t => t is { IsInterface: true, IsPublic: true, IsGenericTypeDefinition: false } && !proprios.Contains(t));
        var registradas = services.Select(s => s.ServiceType).ToHashSet();
        Assert.All(portas, porta => Assert.Contains(porta, registradas));
    }

    // Estrutura física de application.md: módulo simples tem tudo na raiz; o complexo separa contratos, portas,
    // casos de uso e validação. AppService, interface pública e Mapping ficam sempre na raiz da feature.
    [Fact]
    public void FeaturesSeguemAEstruturaDocumentada()
    {
        var raiz = Path.Combine(Raiz(), "src", "Dante.Application");
        var features = Directory.EnumerateDirectories(raiz).Select(Path.GetFileName)
            .Where(nome => nome is not ("bin" or "obj") && !ForaDasFeatures.Contains(nome)).ToArray();
        Assert.NotEmpty(features);
        foreach (var feature in features)
        {
            var pasta = Path.Combine(raiz, feature!);
            var subpastas = Directory.EnumerateDirectories(pasta).Select(Path.GetFileName).ToArray();
            Assert.All(subpastas, sub => Assert.Contains(sub, Subpastas));
            Assert.All(subpastas, sub => Assert.Empty(Directory.EnumerateDirectories(Path.Combine(pasta, sub!))));
            var complexo = subpastas.Length > 0;
            foreach (var arquivo in Directory.EnumerateFiles(pasta, "*.cs", SearchOption.AllDirectories))
            {
                var nome = Path.GetFileNameWithoutExtension(arquivo);
                var local = Path.GetDirectoryName(Path.GetRelativePath(pasta, arquivo)) is { Length: > 0 } sub ? sub : "raiz";
                var esperado = nome switch
                {
                    _ when nome.EndsWith("AppService", StringComparison.Ordinal) || nome.EndsWith("Mapping", StringComparison.Ordinal) => "raiz",
                    _ when !complexo => "raiz",
                    _ when nome.EndsWith("Dto", StringComparison.Ordinal) && local != "Portas" => "Contratos",
                    _ when Regex.IsMatch(nome, "^I[A-Z].*Repository$") => "Portas",
                    _ when nome.EndsWith("Validator", StringComparison.Ordinal) => "Validacao",
                    _ => local
                };
                Assert.True(esperado == local, $"{feature}/{Path.GetRelativePath(pasta, arquivo)} deveria estar em {esperado}.");
            }
        }
    }

    // A tabela "Classificação atual" de application.md acompanha a estrutura real das pastas.
    [Fact]
    public void ClassificacaoDocumentadaCorrespondeAsPastas()
    {
        var raiz = Path.Combine(Raiz(), "src", "Dante.Application");
        var linhas = File.ReadAllLines(Path.Combine(Raiz(), "docs", "development", "application.md"));
        var cabecalho = Array.IndexOf(linhas, "| Complexos | Simples |");
        Assert.True(cabecalho >= 0, "Tabela de classificação ausente em application.md.");
        var colunas = linhas[cabecalho + 2].Split('|', StringSplitOptions.RemoveEmptyEntries);
        string[] Nomes(string coluna) => Regex.Matches(coluna, "`(\\w+)`").Select(m => m.Groups[1].Value).Order().ToArray();
        var features = Directory.EnumerateDirectories(raiz).Select(Path.GetFileName)
            .Where(nome => nome is not ("bin" or "obj") && !ForaDasFeatures.Contains(nome)).ToArray();
        Assert.Equal(features.Where(f => Directory.EnumerateDirectories(Path.Combine(raiz, f!)).Any()).Order(), Nomes(colunas[0]));
        Assert.Equal(features.Where(f => !Directory.EnumerateDirectories(Path.Combine(raiz, f!)).Any()).Order(), Nomes(colunas[1]));
    }

    // Validators são internal static, sem estado nem dependências, e pertencem à feature (#205).
    [Fact]
    public void ValidatorsSaoEstaticosInternosDaFeature()
    {
        var validators = Application.GetTypes().Where(t => t.Name.EndsWith("Validator", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(validators);
        Assert.All(validators, t =>
        {
            // Tipo aninhado nunca é IsPublic; recusá-lo torna !IsPublic equivalente a internal de topo (review do PR #222).
            Assert.False(t.IsNested, $"{t.Name} deve ser tipo de topo da feature.");
            Assert.True(t.IsAbstract && t.IsSealed && !t.IsPublic, $"{t.Name} deve ser internal static.");
            Assert.DoesNotContain(t.Namespace, new[] { "Dante.Application.Comum", "Dante.Application.Mapeamento", "Dante.Application" });
        });
    }

    // Mappings pertencem à feature do contrato que produzem: <Entidade>Mapping fica no namespace de <Entidade>Dto.
    [Fact]
    public void MappingsFicamNaFeatureDoProprioDto()
    {
        var tipos = Application.GetTypes();
        var mappings = tipos.Where(t => t.Name.EndsWith("Mapping", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(mappings);
        Assert.All(mappings, mapping =>
        {
            Assert.False(mapping.IsNested, $"{mapping.Name} deve ser tipo de topo da feature.");
            Assert.Equal(mapping.Namespace, Assert.Single(tipos, t => t.Name == mapping.Name[..^"Mapping".Length] + "Dto").Namespace);
        });
    }

    // Hosts adaptam entrada/saída e compõem; regras, mappings, validações e AppServices ficam na Application.
    [Theory]
    [InlineData("Dante.Worker")]
    [InlineData("Dante.WebApi")]
    public void HostsNaoImplementamRegrasDaApplication(string host)
    {
        var assembly = host == "Dante.Worker" ? typeof(TelegramPollingService).Assembly : typeof(Dante.WebApi.Program).Assembly;
        Assert.Equal(host, assembly.GetName().Name);
        var tipos = assembly.GetTypes();
        Assert.Empty(tipos.SelectMany(t => t.GetInterfaces().Where(i => i.Assembly == Application).Select(i => $"{t.Name} : {i.Name}")));
        Assert.DoesNotContain(tipos, t => Regex.IsMatch(t.Name, "(AppService|Validator|Mapping|Repository)$"));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Mapster", StringComparison.Ordinal));

        var regra = new Regex(@"\bnew\s+\w+AppService\s*\(|\b(TypeAdapterConfig|ConfiguracaoMapeamento|AddMapeamentos|ValidacaoDeEntrada)\b");
        var violacoes = Directory.EnumerateFiles(Path.Combine(Raiz(), "src", host), "*.cs", SearchOption.AllDirectories)
            .Where(arquivo => !arquivo.Split(Path.DirectorySeparatorChar).Any(segmento => segmento is "obj" or "bin"))
            .Where(arquivo => regra.IsMatch(File.ReadAllText(arquivo))).Select(Path.GetFileName);
        Assert.Empty(violacoes);

        var services = new ServiceCollection();
        if (host == "Dante.Worker") services.AddWorker(new ConfigurationBuilder().Build());
        else Dante.WebApi.ConfiguracaoHttp.AddWebApi(services);
        Assert.DoesNotContain(services, s => s.ServiceType.Assembly == Application || s.ImplementationType?.Assembly == Application);
    }

    private static IConfiguration ConfiguracaoComBanco() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = "Host=localhost;Database=nao_conectar" }).Build();

    private static string Raiz()
    {
        for (var diretorio = new DirectoryInfo(AppContext.BaseDirectory); diretorio is not null; diretorio = diretorio.Parent)
            if (File.Exists(Path.Combine(diretorio.FullName, "Dante.sln"))) return diretorio.FullName;
        throw new DirectoryNotFoundException("Solution não encontrada.");
    }
}
