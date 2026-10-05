using System.Diagnostics;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramRepositoryCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-repo-commands-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AuthorizedUserCanAddShowListAndRemove()
    {
        var repository = CreateRepository();
        var api = new BotApi();
        using var service = CreateService(api);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue($"/repo add @Demo {repository} owner/demo");
            Assert.Contains("@demo", await api.NextMessageAsync());
            api.Enqueue("/repo show @DEMO");
            Assert.Contains(repository, await api.NextMessageAsync());
            api.Enqueue("/repos");
            Assert.Contains("owner/demo", await api.NextMessageAsync());
            api.Enqueue($"/repo add @demo {repository}");
            Assert.Contains("já está cadastrado", await api.NextMessageAsync());
            api.Enqueue("/repo remove @demo");
            Assert.Contains("removido", await api.NextMessageAsync());
            api.Enqueue("/repo show @demo");
            Assert.Contains("não cadastrado", await api.NextMessageAsync());
            api.Enqueue("/repos");
            Assert.Contains("Nenhum", await api.NextMessageAsync());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RejectsInvalidInputAndUnauthorizedUser()
    {
        var repository = CreateRepository();
        var api = new BotApi();
        using var service = CreateService(api);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/repo add @bad-name " + repository);
            Assert.Contains("Alias inválido", await api.NextMessageAsync());
            api.Enqueue("/repo add @demo relative/path");
            Assert.Contains("absoluto", await api.NextMessageAsync());
            api.Enqueue("/repo add @demo " + Path.Combine(root, "missing"));
            Assert.Contains("não existe", await api.NextMessageAsync());
            api.Enqueue("/repo remove @missing");
            Assert.Contains("não cadastrado", await api.NextMessageAsync());
            api.Enqueue($"/repo add @hidden {repository}", 999);
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            api.Enqueue("/repos");
            Assert.Contains("Nenhum", await api.NextMessageAsync());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AcceptsQuotedRepositoryPathsWithSpaces()
    {
        var repository = CreateRepository("project with spaces");
        var api = new BotApi();
        using var service = CreateService(api);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue($"/repo add @spaced \"{repository}\" owner/demo");
            Assert.Contains("@spaced", await api.NextMessageAsync());
            api.Enqueue("/repo show @spaced");
            Assert.Contains(repository, await api.NextMessageAsync());
            api.Enqueue($"/repo add @bad \"{repository}");
            Assert.Contains("Aspas não fechadas", await api.NextMessageAsync());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task EnvironmentCommandsMaskBindingsAndDoNotReturnAgentSecrets()
    {
        var repository = CreateRepository();
        var registry = new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json"));
        registry.Add("@demo", repository);
        var api = new BotApi();
        var runner = new UnusedRunner();
        using var service = CreateService(api, registry, runner);
        var previous = Environment.GetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET");
        Environment.SetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET", "secret-value-123");
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/repo env set @demo API_BASE_URL https://example.test");
            Assert.DoesNotContain("https://example.test", await api.NextMessageAsync());
            api.Enqueue("/repo env bind @demo DATABASE_PASSWORD DANTE_TEST_TELEGRAM_SECRET");
            Assert.Contains("vinculada", await api.NextMessageAsync());
            api.Enqueue("/repo env list @demo");
            var list = await api.NextMessageAsync();
            Assert.Contains("DATABASE_PASSWORD (host: DANTE_TEST_TELEGRAM_SECRET)", list);
            Assert.DoesNotContain("secret-value-123", list);
            Assert.DoesNotContain("https://example.test", list);

            Environment.SetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET", null);
            api.Enqueue("/codex @demo run");
            Assert.Contains("não está configurada", await api.NextMessageAsync());
            Assert.Null(runner.Environment);
            Environment.SetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET", "secret-value-123");

            api.Enqueue("/codex @demo run");
            Assert.Contains("iniciado", await api.NextMessageAsync());
            var finished = await api.NextMessageAsync();
            Assert.Contains("Saída omitida", finished);
            Assert.DoesNotContain("secret-value-123", finished);
            Assert.Equal("secret-value-123", runner.Environment!["DATABASE_PASSWORD"]);

            api.Enqueue("/repo env remove @demo DATABASE_PASSWORD");
            Assert.Contains("removida", await api.NextMessageAsync());
            api.Enqueue("/repo env list @demo");
            Assert.DoesNotContain("DATABASE_PASSWORD", await api.NextMessageAsync());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET", previous);
        }
    }

    private TelegramPollingService CreateService(BotApi api, RepositoryRegistry? registry = null, UnusedRunner? runner = null)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        runner ??= new UnusedRunner();
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            registry ?? new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json")));
    }

    private string CreateRepository(string name = "project")
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
        File.AppendAllText(Path.Combine(path, ".git", "config"),
            "\n[remote \"origin\"]\n\turl = https://github.com/owner/demo.git\n");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public void Enqueue(string command, long sender = 123) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(1), command, new TelegramUser(sender))));

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

    private sealed class UnusedRunner : ICodexRunner, IClaudeRunner
    {
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null)
        {
            Environment = environment;
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "secret-value-123", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }
}
