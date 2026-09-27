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
            Options.Create(new TelegramOptions { BotToken = "test-token" }),
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
            NullLogger<TelegramPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, api.PollCount);
    }

    private sealed class StubBotApi : ITelegramBotApi
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
                return [new TelegramUpdate(42, new TelegramMessage(new TelegramChat(-123), "/ping"))];
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
}
