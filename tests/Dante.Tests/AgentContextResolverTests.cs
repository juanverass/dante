using System.Diagnostics;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Jobs;

namespace Dante.Tests;

public sealed class AgentContextResolverTests : IDisposable
{
    private const long UserId = 123;
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-resolver-" + Guid.NewGuid().ToString("N"));

    [Theory]
    // Default Claude, no active repository.
    [InlineData(AgentKind.Claude, null, null, "qual a capital da Grécia?", AgentKind.Claude, AgentSource.Default, "General", ContextSource.General, "qual a capital da Grécia?")]
    [InlineData(AgentKind.Claude, null, AgentKind.Codex, "revise o PR", AgentKind.Codex, AgentSource.Explicit, "General", ContextSource.General, "revise o PR")]
    [InlineData(AgentKind.Claude, null, null, "@dante revise o README", AgentKind.Claude, AgentSource.Default, "@dante", ContextSource.Explicit, "revise o README")]
    [InlineData(AgentKind.Codex, null, null, "olá", AgentKind.Codex, AgentSource.Default, "General", ContextSource.General, "olá")]
    // Default Claude, @fitness_backend active.
    [InlineData(AgentKind.Claude, "@fitness_backend", null, "implemente a issue 500", AgentKind.Claude, AgentSource.Default, "@fitness_backend", ContextSource.Active, "implemente a issue 500")]
    [InlineData(AgentKind.Claude, "@fitness_backend", AgentKind.Codex, "revise o PR", AgentKind.Codex, AgentSource.Explicit, "@fitness_backend", ContextSource.Active, "revise o PR")]
    [InlineData(AgentKind.Claude, "@fitness_backend", AgentKind.Codex, "@dante revise o README", AgentKind.Codex, AgentSource.Explicit, "@dante", ContextSource.Explicit, "revise o README")]
    [InlineData(AgentKind.Codex, "@fitness_backend", AgentKind.Claude, "@DANTE\nrevise", AgentKind.Claude, AgentSource.Explicit, "@dante", ContextSource.Explicit, "revise")]
    // An alias that is not the first token is prompt text, never a context choice.
    [InlineData(AgentKind.Claude, "@fitness_backend", null, "compare com @dante", AgentKind.Claude, AgentSource.Default, "@fitness_backend", ContextSource.Active, "compare com @dante")]
    [InlineData(AgentKind.Claude, null, null, "use /codex @dante", AgentKind.Claude, AgentSource.Default, "General", ContextSource.General, "use /codex @dante")]
    public void ResolvesAgentAndContextByPrecedence(AgentKind defaultAgent, string? active, AgentKind? explicitAgent,
        string text, AgentKind agent, AgentSource agentSource, string label, ContextSource contextSource, string prompt)
    {
        var (resolver, settings) = Create(defaultAgent, active);

        var resolved = resolver.Resolve(UserId, explicitAgent, text);

        Assert.True(resolved.Succeeded, resolved.Error);
        Assert.Equal(agent, resolved.Agent);
        Assert.Equal(agentSource, resolved.AgentSource);
        Assert.Equal(label, resolved.Context!.Label);
        Assert.Equal(contextSource, resolved.ContextSource);
        Assert.Equal(prompt, resolved.Prompt);
        Assert.Equal(label == "General" ? JobExecutionMode.General : JobExecutionMode.Repository, resolved.Context.Mode);
        // Overrides apply to this resolution only.
        Assert.Equal(defaultAgent, settings.Current.DefaultAgent);
        Assert.Equal(active, settings.GetActiveRepository(UserId));
    }

    [Theory]
    [InlineData(null, "", ContextResolutionFailure.EmptyPrompt)]
    [InlineData(null, "   ", ContextResolutionFailure.EmptyPrompt)]
    [InlineData(null, "@dante", ContextResolutionFailure.EmptyPrompt)]
    [InlineData(null, "@dante   ", ContextResolutionFailure.EmptyPrompt)]
    [InlineData(null, "@ghost faça algo", ContextResolutionFailure.UnknownAlias)]
    [InlineData("@fitness_backend", "@ghost faça algo", ContextResolutionFailure.UnknownAlias)]
    [InlineData("@fitness_backend", "@bad-alias faça algo", ContextResolutionFailure.InvalidAlias)]
    [InlineData("@ghost", "implemente a issue 500", ContextResolutionFailure.StaleActiveRepository)]
    public void RefusesWithoutFallingBackToAnotherContext(string? active, string text, ContextResolutionFailure failure)
    {
        var (resolver, _) = Create(AgentKind.Claude, active);

        var resolved = resolver.Resolve(UserId, AgentKind.Codex, text);

        Assert.False(resolved.Succeeded);
        Assert.Equal(failure, resolved.Failure);
        Assert.Null(resolved.Context);
        Assert.Null(resolved.Environment);
        Assert.Equal(failure == ContextResolutionFailure.EmptyPrompt, resolved.Error is null);
    }

    [Fact]
    public void ErrorsNameTheExplicitAliasOrTheStaleActiveRepository()
    {
        var (resolver, settings) = Create(AgentKind.Claude, "@ghost");

        Assert.Equal("Repositório @other não cadastrado. Veja /repos.",
            resolver.Resolve(UserId, null, "@other faça algo").Error);
        Assert.Equal("O repositório ativo @ghost não está mais cadastrado. Use /use @alias ou /use general.",
            resolver.Resolve(UserId, null, "faça algo").Error);
        Assert.Equal("@ghost", settings.GetActiveRepository(UserId));
    }

    [Fact]
    public void ResolveContextUsesTheAliasApartFromAnyPrompt()
    {
        var (resolver, _) = Create(AgentKind.Codex, "@fitness_backend");

        var active = resolver.ResolveContext(UserId, null, null);
        Assert.True(active.Succeeded);
        Assert.Equal((AgentKind.Codex, AgentSource.Default, "@fitness_backend", ContextSource.Active),
            (active.Agent, active.AgentSource, active.Context!.Label, active.ContextSource));

        var explicitContext = resolver.ResolveContext(UserId, AgentKind.Claude, "@dante");
        Assert.True(explicitContext.Succeeded);
        Assert.Equal((AgentKind.Claude, AgentSource.Explicit, "@dante", ContextSource.Explicit),
            (explicitContext.Agent, explicitContext.AgentSource, explicitContext.Context!.Label, explicitContext.ContextSource));
        Assert.Equal(string.Empty, explicitContext.Prompt);
    }

    [Fact]
    public void WithoutSettingsUsesClaudeAndGeneral()
    {
        var general = new GeneralWorkspace(Path.Combine(root, "general"));
        var resolved = new AgentContextResolver(general).Resolve(UserId, null, "olá");

        Assert.Equal((AgentKind.Claude, AgentSource.Default, ContextSource.General, general.Path),
            (resolved.Agent, resolved.AgentSource, resolved.ContextSource, resolved.Context!.WorkingDirectory));
    }

    [Fact]
    public void MissingHostBindingRefusesTheRepository()
    {
        var (resolver, _) = Create(AgentKind.Claude, null, out var registry);
        registry.Bind("@dante", "API_TOKEN", "DANTE_RESOLVER_MISSING_" + Guid.NewGuid().ToString("N"));

        var resolved = resolver.Resolve(UserId, null, "@dante rode");

        Assert.Equal(ContextResolutionFailure.Environment, resolved.Failure);
        Assert.NotNull(resolved.Error);
    }

    [Fact]
    public void GeneralWorkspaceInsideARepositoryIsRefused()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "config", "repositories.json"));
        var repository = CreateGitRepository("repo");
        registry.Add("@repo", repository);
        var resolver = new AgentContextResolver(new GeneralWorkspace(Path.Combine(repository, "general")), registry);

        Assert.Equal(ContextResolutionFailure.GeneralWorkspaceOverlap, resolver.Resolve(UserId, null, "olá").Failure);
        Assert.True(resolver.Resolve(UserId, null, "@repo olá").Succeeded);
    }

    // #232 (AD-58): the primary comes from the usual precedence and brings its registered links, frozen with it.
    [Theory]
    [InlineData(null, "@fitness_front crie o endpoint", ContextSource.Explicit)]
    [InlineData("@fitness_front", "crie o endpoint", ContextSource.Active)]
    public void LinkedRepositoriesFollowThePrimary(string? active, string text, ContextSource source)
    {
        var (resolver, _) = Create(AgentKind.Claude, active, out var registry);
        var front = CreateGitRepository("fitness_front");
        registry.Add("@fitness_front", front);
        registry.Link("@fitness_front", "@fitness_backend");
        registry.Bind("@fitness_backend", "API_TOKEN", "DANTE_RESOLVER_MISSING_" + Guid.NewGuid().ToString("N"));

        var resolved = resolver.Resolve(UserId, null, text);

        Assert.True(resolved.Succeeded, resolved.Error);
        Assert.Equal((source, "@fitness_front", front), (resolved.ContextSource, resolved.Context!.Label,
            resolved.Context.WorkingDirectory));
        var linked = Assert.Single(resolved.Context.RepositoriosVinculados);
        Assert.Equal(("@fitness_backend", registry.Get("@fitness_backend")!.Path), (linked.Alias, linked.Caminho));
        Assert.Equal("@fitness_front + @fitness_backend", resolved.Context.Descricao);
        // Only the primary's environment: the backend's missing binding does not even get resolved.
        Assert.Empty(resolved.Environment!.Values);

        // The snapshot does not follow later catalog changes (AD-11).
        registry.Unlink("@fitness_front", "@fitness_backend");
        Assert.Single(resolved.Context.RepositoriosVinculados);
        Assert.Empty(resolver.Resolve(UserId, null, text).Context!.RepositoriosVinculados);
    }

    // #232 review: a started execution keeps the physical checkout it resolved. Repointing a symlink in the catalog
    // afterwards only changes new resolutions, never one that already started.
    [Fact]
    public void RepointingASymlinkDoesNotMoveAStartedExecution()
    {
        var file = Path.Combine(root, "config", "repositories.json");
        var registry = new RepositoryRegistry(file);
        registry.Add("@front", CreateGitRepository("front"));
        var first = CreateGitRepository("back_v1");
        var second = CreateGitRepository("back_v2");
        registry.Add("@back", first);
        registry.Link("@front", "@back");
        var shortcut = Path.Combine(root, "back_atual");
        Directory.CreateSymbolicLink(shortcut, first);
        File.WriteAllText(file, File.ReadAllText(file).Replace(System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(shortcut)));
        var resolver = new AgentContextResolver(new GeneralWorkspace(Path.Combine(root, "general")),
            new RepositoryRegistry(file));

        var started = resolver.Resolve(UserId, null, "@front crie").Context!;
        Directory.Delete(shortcut);
        Directory.CreateSymbolicLink(shortcut, second);

        Assert.Equal(first, Assert.Single(started.RepositoriosVinculados).Caminho);
        Assert.Equal(second, Assert.Single(resolver.Resolve(UserId, null, "@front crie").Context!.RepositoriosVinculados).Caminho);
    }

    [Fact]
    public void LinksOfTheActiveRepositoryDoNotFollowAnExplicitOverride()
    {
        var (resolver, _) = Create(AgentKind.Claude, "@fitness_front", out var registry);
        registry.Add("@fitness_front", CreateGitRepository("fitness_front"));
        registry.Link("@fitness_front", "@fitness_backend");

        var resolved = resolver.Resolve(UserId, null, "@dante revise");

        Assert.Equal(("@dante", "@dante"), (resolved.Context!.Label, resolved.Context.Descricao));
        Assert.Empty(resolved.Context.RepositoriosVinculados);
    }

    [Fact]
    public void BrokenLinkRefusesWithoutFallingBack()
    {
        var catalog = new InMemoryCatalog(new RepositorioCadastrado("@front", "/repos/front"))
        {
            BrokenLink = "O repositório vinculado @back não está mais cadastrado. Use /repo unlink @front @back."
        };
        var resolver = new AgentContextResolver(new InMemoryWorkspace("/workspaces/general"), catalog,
            new InMemoryPreferences(AgentKind.Claude, "@front"));

        foreach (var text in new[] { "crie o endpoint", "@front crie o endpoint" })
        {
            var resolved = resolver.Resolve(UserId, null, text);
            Assert.Equal((ContextResolutionFailure.InvalidLink, catalog.BrokenLink), (resolved.Failure, resolved.Error));
            Assert.Null(resolved.Context);
            Assert.Null(resolved.Environment);
        }
    }

    [Theory]
    [InlineData("/repos/front/back")]
    [InlineData("/repos")]
    [InlineData("/repos/front")]
    public void OverlappingCheckoutsAreRefused(string linkedPath)
    {
        var catalog = new InMemoryCatalog(new RepositorioCadastrado("@front", "/repos/front"));
        catalog.Links["@front"] = [new("@shared", "/repos/shared"), new("@back", linkedPath)];
        var resolver = new AgentContextResolver(new InMemoryWorkspace("/workspaces/general"), catalog);

        var resolved = resolver.Resolve(UserId, null, "@front crie o endpoint");

        Assert.Equal(ContextResolutionFailure.InvalidLink, resolved.Failure);
        Assert.Contains("se sobrepõem", resolved.Error);
    }

    // General Mode never reaches a repository, linked or not (AD-09).
    [Fact]
    public void GeneralModeHasNoLinkedRepositories()
    {
        var catalog = new InMemoryCatalog(new RepositorioCadastrado("@front", "/repos/front"));
        catalog.Links["@front"] = [new("@back", "/repos/back")];
        var resolver = new AgentContextResolver(new InMemoryWorkspace("/workspaces/general"), catalog);

        var resolved = resolver.Resolve(UserId, null, "olá");

        Assert.Equal((JobExecutionMode.General, "General"), (resolved.Context!.Mode, resolved.Context.Descricao));
        Assert.Empty(resolved.Context.RepositoriosVinculados);
        Assert.Null(ContratoMultirrepositorio.Para(resolved.Context));
    }

    // #166: the use case lives in the Application and runs on its ports alone, without any Worker adapter.
    [Fact]
    public void ResolvesThroughApplicationPortsWithoutWorkerAdapters()
    {
        var catalog = new InMemoryCatalog(new RepositorioCadastrado("@dante", "/repos/dante"));
        var preferences = new InMemoryPreferences(AgentKind.Codex, "@dante");
        var resolver = new AgentContextResolver(new InMemoryWorkspace("/workspaces/general"), catalog, preferences);

        var active = resolver.Resolve(UserId, null, "revise");
        Assert.Equal((AgentKind.Codex, ContextSource.Active, "/repos/dante", true),
            (active.Agent, active.ContextSource, active.Context!.WorkingDirectory, active.Environment!.HasSecrets));
        Assert.Equal(ContextResolutionFailure.InvalidAlias, resolver.Resolve(UserId, null, "@inválido x").Failure);
        Assert.Equal(ContextResolutionFailure.UnknownAlias, resolver.Resolve(UserId, null, "@outro x").Failure);

        preferences.Active = "@removido";
        Assert.Equal(ContextResolutionFailure.StaleActiveRepository, resolver.Resolve(UserId, null, "x").Failure);
        preferences.Active = null;
        var general = resolver.Resolve(UserId, AgentKind.Claude, "x");
        Assert.Equal((AgentKind.Claude, AgentSource.Explicit, "/workspaces/general"),
            (general.Agent, general.AgentSource, general.Context!.WorkingDirectory));
        Assert.DoesNotContain(typeof(AgentContextResolver).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "Dante.Worker");
    }

    private (AgentContextResolver Resolver, AssistantSettingsStore Settings) Create(AgentKind defaultAgent,
        string? active) => Create(defaultAgent, active, out _);

    private (AgentContextResolver Resolver, AssistantSettingsStore Settings) Create(AgentKind defaultAgent,
        string? active, out RepositoryRegistry registry)
    {
        registry = new RepositoryRegistry(Path.Combine(root, "config", "repositories.json"));
        registry.Add("@fitness_backend", CreateGitRepository("fitness_backend"));
        registry.Add("@dante", CreateGitRepository("dante"));
        var settings = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        settings.SetDefaultAgent(defaultAgent);
        if (active is not null) settings.SetActiveRepository(UserId, active);
        return (new AgentContextResolver(new GeneralWorkspace(Path.Combine(root, "general")), registry, settings),
            settings);
    }

    private string CreateGitRepository(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        using var process = Process.Start(new ProcessStartInfo("git")
        {
            WorkingDirectory = path, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, ArgumentList = { "init" }
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class InMemoryWorkspace(string path) : IWorkspaceGeral
    {
        public string Caminho => path;
    }

    private sealed class InMemoryPreferences(AgentKind defaultAgent, string? active) : IPreferenciasDoAssistente
    {
        public string? Active { get; set; } = active;
        public AssistantSettings Atual { get; } = new(defaultAgent);
        public string? ObterRepositorioAtivo(long idUsuario) => Active;
    }

    private sealed class InMemoryCatalog(params RepositorioCadastrado[] repositories) : ICatalogoDeRepositorios
    {
        public IReadOnlyList<RepositorioCadastrado> Listar() => repositories;

        public RepositorioCadastrado? Obter(string alias) =>
            alias.All(character => char.IsAsciiLetterOrDigit(character) || character is '@' or '_')
                ? repositories.FirstOrDefault(repository => repository.Alias == alias)
                : throw new ArgumentException("Alias inválido.", nameof(alias));

        public ResolvedRepositoryEnvironment ResolverAmbiente(string alias) =>
            new(new Dictionary<string, string> { ["API_TOKEN"] = "valor" }, true);

        // Direct links by primary alias; a broken link answers like the real catalog, with InvalidOperationException.
        public Dictionary<string, RepositorioCadastrado[]> Links { get; } = [];

        public string? BrokenLink { get; set; }

        public IReadOnlyList<RepositorioCadastrado> ResolverVinculos(string alias) =>
            BrokenLink is { } broken ? throw new InvalidOperationException(broken) : Links.GetValueOrDefault(alias) ?? [];
    }
}
