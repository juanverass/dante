using System.Diagnostics;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
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

    private TelegramPollingService CreateService(BotApi api)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        var runner = new UnusedRunner();
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json")));
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
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
