using System.Diagnostics;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramActiveRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-active-repo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ActiveRepositoryIsUsedUntilGeneralIsRestored()
    {
        var (registry, first, second) = CreateRegistry();
        var file = Path.Combine(root, "settings.json");
        var api = new BotApi();
        var codex = new RecordingRunner();
        var claude = new RecordingRunner();
        var general = new GeneralWorkspace(Path.Combine(root, "general"));
        using var service = CreateService(api, registry, new AssistantSettingsStore(file), codex, claude, general);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/use");
            Assert.Equal("Contexto ativo: General", await api.NextMessageAsync());
            api.Enqueue("/use @FIRST");
            Assert.Equal("Contexto ativo: @first", await api.NextMessageAsync());
            api.Enqueue("/use");
            Assert.Equal("Contexto ativo: @first", await api.NextMessageAsync());
            Assert.Equal("@first", new AssistantSettingsStore(file).GetActiveRepository(123));

            await RunJob(api, "implemente a issue 500", "(@first)");
            Assert.Equal(new Run("implemente a issue 500", first, false), claude.Runs[0]);
            await RunJob(api, "/codex revise o PR", "(@first)");
            Assert.Equal(new Run("revise o PR", first, false), codex.Runs[0]);
            await RunJob(api, "/codex @second revise o README", "(@second)");
            Assert.Equal(new Run("revise o README", second, false), codex.Runs[1]);

            api.Enqueue("/use");
            Assert.Equal("Contexto ativo: @first", await api.NextMessageAsync());
            await RunJob(api, "continue", "(@first)");
            Assert.Equal(new Run("continue", first, false), claude.Runs[1]);

            api.Enqueue("/use GENERAL");
            Assert.Equal("Contexto ativo: General", await api.NextMessageAsync());
            await RunJob(api, "qual a capital da Grécia?", "(General)");
            Assert.Equal(new Run("qual a capital da Grécia?", general.Path, true), claude.Runs[2]);
            Assert.Null(new AssistantSettingsStore(file).GetActiveRepository(123));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UsersKeepIndependentActiveRepositories()
    {
        var (registry, first, second) = CreateRegistry();
        var api = new BotApi();
        var claude = new RecordingRunner();
        var general = new GeneralWorkspace(Path.Combine(root, "general"));
        using var service = CreateService(api, registry, new AssistantSettingsStore(Path.Combine(root, "settings.json")),
            new RecordingRunner(), claude, general);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/use @first", 123);
            Assert.Equal("Contexto ativo: @first", await api.NextMessageAsync());
            api.Enqueue("/use @second", 456);
            Assert.Equal("Contexto ativo: @second", await api.NextMessageAsync());
            api.Enqueue("/use", 123);
            Assert.Equal("Contexto ativo: @first", await api.NextMessageAsync());

            await RunJob(api, "from first user", "(@first)", 123);
            await RunJob(api, "from second user", "(@second)", 456);
            Assert.Equal(new Run("from first user", first, false), claude.Runs[0]);
            Assert.Equal(new Run("from second user", second, false), claude.Runs[1]);

            api.Enqueue("/use general", 456);
            Assert.Equal("Contexto ativo: General", await api.NextMessageAsync());
            await RunJob(api, "still first", "(@first)", 123);
            Assert.Equal(new Run("still first", first, false), claude.Runs[2]);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("/use @missing", "não cadastrado")]
    [InlineData("/use @bad-alias", "Alias inválido")]
    [InlineData("/use first", "Uso:")]
    [InlineData("/use @first @second", "Uso:")]
    public async Task InvalidSelectionKeepsCurrentContextWithoutStartingJob(string command, string expected)
    {
        var (registry, _, _) = CreateRegistry();
        var store = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        store.SetActiveRepository(123, "@second");
        var api = new BotApi();
        var claude = new RecordingRunner();
        using var service = CreateService(api, registry, store, new RecordingRunner(), claude);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue(command);
            Assert.Contains(expected, await api.NextMessageAsync());
            Assert.Equal("@second", store.GetActiveRepository(123));
            Assert.Empty(claude.Runs);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RemovingRepositoryClearsActiveContexts()
    {
        var (registry, _, _) = CreateRegistry();
        var file = Path.Combine(root, "settings.json");
        var api = new BotApi();
        var claude = new RecordingRunner();
        var general = new GeneralWorkspace(Path.Combine(root, "general"));
        using var service = CreateService(api, registry, new AssistantSettingsStore(file), new RecordingRunner(),
            claude, general);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/use @first", 123);
            Assert.Equal("Contexto ativo: @first", await api.NextMessageAsync());
            api.Enqueue("/use @first", 456);
            Assert.Equal("Contexto ativo: @first", await api.NextMessageAsync());
            api.Enqueue("/repo remove @first");
            Assert.Equal("Repositório removido.", await api.NextMessageAsync());

            api.Enqueue("/use", 456);
            Assert.Equal("Contexto ativo: General", await api.NextMessageAsync());
            Assert.Null(new AssistantSettingsStore(file).GetActiveRepository(123));
            await RunJob(api, "after removal", "(General)");
            Assert.Equal(new Run("after removal", general.Path, true), Assert.Single(claude.Runs));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StaleActiveRepositoryRequiresNewSelection()
    {
        var (registry, _, second) = CreateRegistry();
        var store = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        store.SetActiveRepository(123, "@ghost");
        var api = new BotApi();
        var claude = new RecordingRunner();
        using var service = CreateService(api, registry, store, new RecordingRunner(), claude);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/use");
            Assert.Contains("@ghost (não cadastrado", await api.NextMessageAsync());
            api.Enqueue("implemente a issue 500");
            Assert.Contains("O repositório ativo @ghost não está mais cadastrado", await api.NextMessageAsync());
            api.Enqueue("/claude implemente a issue 500");
            Assert.Contains("O repositório ativo @ghost não está mais cadastrado", await api.NextMessageAsync());
            Assert.Empty(claude.Runs);

            await RunJob(api, "@second explicit alias still works", "(@second)");
            Assert.Equal(new Run("explicit alias still works", second, false), Assert.Single(claude.Runs));
            Assert.Equal("@ghost", store.GetActiveRepository(123));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UnauthorizedUserCannotSelectRepository()
    {
        var (registry, _, _) = CreateRegistry();
        var store = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        var api = new BotApi();
        using var service = CreateService(api, registry, store, new RecordingRunner(), new RecordingRunner());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/use @first", 999);
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            Assert.Null(store.GetActiveRepository(999));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private (RepositoryRegistry Registry, string First, string Second) CreateRegistry()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "config", "repositories.json"));
        var first = CreateGitRepository("first");
        var second = CreateGitRepository("second");
        registry.Add("@first", first);
        registry.Add("@second", second);
        return (registry, first, second);
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

    private TelegramPollingService CreateService(BotApi api, RepositoryRegistry registry, AssistantSettingsStore settings,
        RecordingRunner codex, RecordingRunner claude, GeneralWorkspace? general = null)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), codex, claude,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance, registry,
            general ?? new GeneralWorkspace(Path.Combine(root, "general")), settings);
    }

    private static async Task RunJob(BotApi api, string command, string label, long senderId = 123)
    {
        api.Enqueue(command, senderId);
        var started = await api.NextMessageAsync();
        Assert.Contains("iniciado", started);
        Assert.Contains(label, started);
        Assert.Contains("concluído", await api.NextMessageAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Run(string Prompt, string WorkingDirectory, bool GeneralMode);

    private sealed class RecordingRunner : ICodexRunner, IClaudeRunner
    {
        private readonly List<Run> runs = [];

        public IReadOnlyList<Run> Runs
        {
            get { lock (runs) return runs.ToArray(); }
        }

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null)
        {
            lock (runs) runs.Add(new Run(prompt, workingDirectory, generalMode));
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public void Enqueue(string command, long senderId = 123) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(1), command, new TelegramUser(senderId))));

        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
    }
}
