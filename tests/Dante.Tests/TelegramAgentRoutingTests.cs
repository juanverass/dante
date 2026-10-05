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

public sealed class TelegramAgentRoutingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-routing-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoutesGeneralAndMultipleRepositoriesWithoutGuessing()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "config", "repositories.json"));
        var first = CreateGitRepository("first");
        var second = CreateGitRepository("second");
        registry.Add("@first", first);
        registry.Add("@second", second);
        var general = new GeneralWorkspace(Path.Combine(root, "general"));
        var api = new BotApi();
        var codex = new RecordingRunner();
        var claude = new RecordingRunner();
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            codex, claude, new JobRegistry(), NullLogger<TelegramPollingService>.Instance, registry, general);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await RunCommand(api, "/codex explain @second", true);
            Assert.Equal(new Run("explain @second", general.Path, true), Assert.Single(codex.Runs));

            await RunCommand(api, "/codex @FIRST fix it", true);
            Assert.Equal(new Run("fix it", first, false), codex.Runs[1]);

            await RunCommand(api, "/claude @second review", true);
            Assert.Equal(new Run("review", second, false), Assert.Single(claude.Runs));

            await RunCommand(api, "/codex @unknown fix it", false);
            Assert.Equal(2, codex.Runs.Count);
            await RunCommand(api, "/claude @second", false);
            Assert.Single(claude.Runs);

            await RunCommand(api, "@first plain message", true);
            Assert.Equal(new Run("plain message", first, false), claude.Runs[1]);
            await RunCommand(api, "@unknown plain message", false);
            Assert.Equal(2, claude.Runs.Count);
        }
        finally { await service.StopAsync(CancellationToken.None); }
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

    private static async Task RunCommand(BotApi api, string command, bool startsJob)
    {
        api.Enqueue(command);
        var first = await api.NextMessageAsync();
        if (startsJob)
        {
            Assert.Contains("iniciado", first);
            Assert.Contains("concluído", await api.NextMessageAsync());
        }
        else Assert.True(first.Contains("não cadastrado") || first.Contains("Uso:"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Run(string Prompt, string WorkingDirectory, bool GeneralMode);

    private sealed class RecordingRunner : ICodexRunner, IClaudeRunner
    {
        public List<Run> Runs { get; } = [];
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null)
        {
            Runs.Add(new Run(prompt, workingDirectory, generalMode));
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public void Enqueue(string command) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(1), command, new TelegramUser(123))));

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
