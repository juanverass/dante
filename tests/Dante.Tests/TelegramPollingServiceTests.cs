using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramPollingServiceTests
{
    [Fact]
    public async Task RecoversFromTransientFailureAndRepliesToOriginChat()
    {
        var api = new StubBotApi();
        using var service = new TelegramPollingService(
            api,
            Options.Create(new TelegramOptions { BotToken = "test-token", AllowedUserIds = "123" }),
            new TelegramUserAuthorizer(Options.Create(new TelegramOptions { AllowedUserIds = "123" })),
            new UnusedRunner(),
            new UnusedRunner(),
            new JobRegistry(),
            NullLogger<TelegramPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await api.Replied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(43, await api.NextOffset.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(api.PollCount >= 3);
            Assert.Equal((-123L, "pong"), Assert.Single(api.Messages));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MissingTokenDisablesPolling()
    {
        var api = new StubBotApi();
        using var service = new TelegramPollingService(
            api,
            Options.Create(new TelegramOptions()),
            new TelegramUserAuthorizer(Options.Create(new TelegramOptions())),
            new UnusedRunner(),
            new UnusedRunner(),
            new JobRegistry(),
            NullLogger<TelegramPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, api.PollCount);
    }

    [Theory]
    [InlineData("", 123L)]
    [InlineData("123", 999L)]
    [InlineData("123", null)]
    [InlineData("123,invalid", 123L)]
    public async Task DoesNotReplyToUnauthorizedSender(string allowedUserIds, long? senderId)
    {
        var api = new StubBotApi(senderId);
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "test-token",
            AllowedUserIds = allowedUserIds
        });
        using var service = new TelegramPollingService(
            api,
            options,
            new TelegramUserAuthorizer(options),
            new UnusedRunner(),
            new UnusedRunner(),
            new JobRegistry(),
            NullLogger<TelegramPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(43, await api.NextOffset.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(api.Messages);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class StubBotApi(long? senderId = 123) : ITelegramBotApi
    {
        public TaskCompletionSource Replied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<long> NextOffset { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(long ChatId, string Text)> Messages { get; } = [];
        public int PollCount { get; private set; }

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
            long offset, CancellationToken cancellationToken)
        {
            PollCount++;
            if (PollCount == 1)
            {
                throw new HttpRequestException("Temporary failure");
            }

            if (PollCount == 2)
            {
                Assert.Equal(0, offset);
                return [new TelegramUpdate(42, new TelegramMessage(
                    new TelegramChat(-123), "/ping",
                    senderId is long id ? new TelegramUser(id) : null))];
            }

            NextOffset.TrySetResult(offset);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Messages.Add((chatId, text));
            Replied.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Runner não deveria ser chamado.");
    }
}
