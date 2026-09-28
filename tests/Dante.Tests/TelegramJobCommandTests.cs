using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
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
            Assert.Contains("J000001 Codex: Running", active);

            api.Enqueue("/cancel J000001");
            var first = await api.NextMessageAsync();
            var second = await api.NextMessageAsync();
            Assert.Contains(new[] { first, second }, message => message.Contains("Cancelamento solicitado para J000001"));
            Assert.Contains(new[] { first, second }, message => message.Contains("Codex cancelado. Job J000001"));
            Assert.True(runner.Cancelled);

            api.Enqueue("/status");
            Assert.Contains("J000001 Codex: Cancelled", await api.NextMessageAsync());
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
            Assert.Contains("J000001 Claude: Succeeded", await api.NextMessageAsync());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
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
            CancellationToken cancellationToken = default, bool generalMode = false)
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
            CancellationToken cancellationToken = default, bool generalMode = false) =>
            Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
}
