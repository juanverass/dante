using System.Threading.Channels;
using System.Diagnostics;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramJobCommandTests
{
    [Fact]
    public async Task StatusAndCancelWorkWhileAgentIsRunningAndPollingContinues()
    {
        var api = new InteractiveBotApi();
        var runner = new BlockingRunner();
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "test-token",
            AllowedUserIds = "123",
            AgentWorkingDirectory = "/tmp/agent-work"
        });
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            runner, runner, new JobRegistry(), NullLogger<TelegramPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/codex keep working");
            var started = await api.NextMessageAsync();
            Assert.Contains("Job ID: J000001", started);
            await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            api.Enqueue("/status");
            var active = await api.NextMessageAsync();
            Assert.Contains("J000001 Codex General: Running", active);

            api.Enqueue("/cancel J000001");
            var first = await api.NextMessageAsync();
            var second = await api.NextMessageAsync();
            Assert.Contains(new[] { first, second }, message => message.Contains("Cancelamento solicitado para J000001"));
            Assert.Contains(new[] { first, second }, message => message.Contains("Codex cancelado. Job J000001"));
            Assert.True(runner.Cancelled);

            api.Enqueue("/status");
            Assert.Contains("J000001 Codex General: Cancelled", await api.NextMessageAsync());
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnknownAndFinishedJobsCannotBeCancelled()
    {
        var api = new InteractiveBotApi();
        var runner = new ImmediateRunner();
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "test-token",
            AllowedUserIds = "123",
            AgentWorkingDirectory = "/tmp/agent-work"
        });
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            runner, runner, new JobRegistry(), NullLogger<TelegramPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/cancel");
            Assert.Equal("Uso: /cancel <jobId>", await api.NextMessageAsync());
            api.Enqueue("/cancel unknown");
            Assert.Contains("não encontrado", await api.NextMessageAsync());
            api.Enqueue("/claude done");
            Assert.Contains("J000001", await api.NextMessageAsync());
            Assert.Contains("concluído", await api.NextMessageAsync());
            api.Enqueue("/cancel J000001");
            Assert.Contains("já encerrado", await api.NextMessageAsync());
            api.Enqueue("/status");
            Assert.Contains("J000001 Claude General: Succeeded", await api.NextMessageAsync());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StatusListsOnlyTheRequestingUsersSessionsApartFromJobs()
    {
        var api = new InteractiveBotApi();
        var runner = new ImmediateRunner();
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "test-token",
            AllowedUserIds = "123",
            AgentWorkingDirectory = "/tmp/agent-work"
        });
        await using var sessions = new SessionRegistry(new FakeSessionDriverFactory(),
            NullLogger<SessionRegistry>.Instance);
        await sessions.StartAsync(new SessionStartRequest(999, AgentKind.Claude,
            JobExecutionContext.General("/tmp/general")));
        await sessions.StartAsync(new SessionStartRequest(123, AgentKind.Codex,
            JobExecutionContext.Repository("@dante", "/repos/dante"), Profile: AgentPermissionProfile.Plan));
        await sessions.SubmitAsync(123, null, "tarefa");
        await sessions.SubmitAsync(123, null, "depois");
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            runner, runner, new JobRegistry(), NullLogger<TelegramPollingService>.Instance, sessions: sessions);

        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/status");
            var status = await api.NextMessageAsync();
            Assert.StartsWith("Nenhum job registrado.\n\nSessões:\n", status);
            Assert.Contains("S000002 Codex @dante: Running (ativa) | turno T000001 | 1 na fila | perfil Plan", status);
            Assert.DoesNotContain("S000001", status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StatusShowsSimultaneousContextsAndKeepsResolvedRepositoryPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "dante-job-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = CreateRepository(Path.Combine(root, "first"));
            var second = CreateRepository(Path.Combine(root, "second"));
            var registry = new RepositoryRegistry(Path.Combine(root, "repositories.json"));
            registry.Add("@fitness_backend", first);
            var general = new GeneralWorkspace(Path.Combine(root, "general"));
            var api = new InteractiveBotApi();
            var runner = new BlockingRunner();
            var jobs = new JobRegistry();
            var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
            using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
                runner, runner, jobs, NullLogger<TelegramPollingService>.Instance, registry, general);
            await service.StartAsync(CancellationToken.None);
            try
            {
                api.Enqueue("/claude general question");
                Assert.Contains("General", await api.NextMessageAsync());
                await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                api.Enqueue("/claude @fitness_backend project question");
                Assert.Contains("@fitness_backend", await api.NextMessageAsync());

                api.Enqueue("/status");
                var status = await api.NextMessageAsync();
                Assert.Contains("J000001 Claude General: Running", status);
                Assert.Contains("J000002 Claude @fitness_backend: Running", status);

                registry.Remove("@fitness_backend");
                registry.Add("@fitness_backend", second);
                Assert.Equal(first, jobs.GetVisible().Single(job => job.Id == "J000002").Context.WorkingDirectory);
                Assert.Equal("@fitness_backend", jobs.GetVisible().Single(job => job.Id == "J000002").Context.RepositoryAlias);

                api.Enqueue("/cancel J000001");
                var firstMessages = new[] { await api.NextMessageAsync(), await api.NextMessageAsync() };
                Assert.Contains(firstMessages, text => text.Contains("J000001 (General)"));
                api.Enqueue("/cancel J000002");
                var secondMessages = new[] { await api.NextMessageAsync(), await api.NextMessageAsync() };
                Assert.Contains(secondMessages, text => text.Contains("J000002 (@fitness_backend)"));
            }
            finally { await service.StopAsync(CancellationToken.None); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string CreateRepository(string path)
    {
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

    private sealed class InteractiveBotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public void Enqueue(string command) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId),
            new TelegramMessage(new TelegramChat(-123), command, new TelegramUser(123))));

        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset,
            CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Assert.Equal(-123, chatId);
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingRunner : ICodexRunner, IClaudeRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }

        public async Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled = true;
                throw;
            }

            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class ImmediateRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
}
