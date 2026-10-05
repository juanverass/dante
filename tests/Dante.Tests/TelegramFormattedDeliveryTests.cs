using System.Collections.Concurrent;
using System.Net;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

public sealed class TelegramFormattedDeliveryTests
{
    [Fact]
    public async Task LargeJobResponseIsFormattedAndResendPreservesFailedPart()
    {
        var api = new FormattedApi { FailOnAttempt = 2 };
        var delivery = Delivery(api);
        var code = string.Concat(Enumerable.Repeat("a < b && c > d\n", 1000));
        await delivery.DeliverJobAsync("J1", 123, -123, "Antes\n```csharp\n" + code + "```\nFim", default);
        Assert.Equal(TelegramDeliveryState.Failed, delivery.Get("J1", 123)!.State);
        var failed = api.Attempts.Last();
        api.FailOnAttempt = 0;
        var retried = await delivery.RetryAsync("J1", 123, -123, default);
        Assert.Equal(TelegramDeliveryState.Delivered, retried!.State);
        Assert.Equal(failed, api.Attempts.ElementAt(2));
        Assert.Equal("Antes\n" + code + "Fim", string.Concat(api.Sent.Select(p => p.PlainText)));
        Assert.All(api.Sent, TelegramMessageFormatterTests.AssertValid);
    }

    [Fact]
    public async Task MarkupRejectionFallsBackToPlainTextAndContinuesWithRemainingParts()
    {
        var api = new FormattedApi { RejectMarkup = true };
        var delivery = Delivery(api);
        var code = string.Concat(Enumerable.Repeat("print('<b>&')\n", 1000));
        await delivery.DeliverJobAsync("J1", 123, -123, "```python\n" + code + "```\nFim", default);
        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get("J1", 123)!.State);
        Assert.Equal(code + "Fim", string.Concat(api.PlainSent));
        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task FailureOfPlainFallbackCanBeResentWithoutRetryingRejectedMarkup()
    {
        var api = new FormattedApi { RejectMarkup = true, FailPlain = true };
        var delivery = Delivery(api);
        await delivery.DeliverJobAsync("J1", 123, -123, "```diff\n-old\n+new\n```", default);
        Assert.Equal(TelegramDeliveryState.Failed, delivery.Get("J1", 123)!.State);
        api.FailPlain = false;
        await delivery.RetryAsync("J1", 123, -123, default);
        Assert.Single(api.Attempts);
        Assert.Equal("-old\n+new\n", Assert.Single(api.PlainSent));
    }

    [Fact]
    public async Task TransientFailureRetriesTheExactFormattedContent()
    {
        var api = new FormattedApi { FailOnAttempt = 1, FailureStatus = HttpStatusCode.InternalServerError };
        var delivery = Delivery(api);
        await delivery.DeliverJobAsync("J1", 123, -123, "```bash\ndotnet test\n```", default);
        Assert.Equal(2, api.Attempts.Count);
        Assert.Equal(api.Attempts.First(), api.Attempts.Last());
        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get("J1", 123)!.State);
    }

    [Fact]
    public async Task StreamingFencesSplitAcrossEventsAndBatchesKeepTheirLanguageAndPrefix()
    {
        var api = new FormattedApi();
        var delivery = Delivery(api);
        delivery.RegisterSession("S000001", 123, -123, false);
        var session = Snapshot();
        await Publish(delivery, session, new MessageDeltaEvent("item", "Antes\n``"));
        await Eventually(() => api.Sent.Count == 1);
        await Publish(delivery, session, new MessageDeltaEvent("item", "`python\nprint(1)\n"));
        await Eventually(() => api.Sent.Count == 2);
        await Publish(delivery, session, new MessageDeltaEvent("item", "print(2)\n```\nDepois"));
        await Publish(delivery, session, new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => delivery.Get(session.Id, 123)?.State == TelegramDeliveryState.Delivered);
        Assert.Contains("language-python", api.Sent.ElementAt(1).Html);
        Assert.Contains("language-python", api.Sent.ElementAt(2).Html);
        Assert.All(api.Sent, part =>
        {
            Assert.StartsWith("[S000001] ", part.PlainText);
            Assert.InRange(part.Html.Length, 1, 4000);
            TelegramMessageFormatterTests.AssertValid(part);
        });
    }

    [Fact]
    public async Task MultilineToolCommandIsASeparateBashBlockWithRelativePaths()
    {
        var api = new FormattedApi();
        var delivery = Delivery(api);
        var session = Snapshot();
        delivery.RegisterSession(session.Id, 123, -123, false);
        delivery.SetActiveSession(123, session.Id);
        await Publish(delivery, session, new ToolStartedEvent("cmd", AgentToolKind.Command,
            "python3 - <<'EOF'\np = '/tmp/general/src/app.py'\nprint('<script>')\nEOF"));
        await Publish(delivery, session, new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => delivery.Get(session.Id, 123)?.State == TelegramDeliveryState.Delivered);
        var part = Assert.Single(api.Sent);
        Assert.Contains("language-bash", part.Html);
        Assert.Contains("src/app.py", part.PlainText);
        Assert.DoesNotContain("/tmp/general", part.PlainText);
        Assert.DoesNotContain("<script>", part.Html);
        TelegramMessageFormatterTests.AssertValid(part);
    }

    [Fact]
    public async Task InlineApprovalKeepsItsKeyboardWhenMarkupFallsBack()
    {
        var api = new FormattedApi { RejectMarkup = true };
        var delivery = Delivery(api);
        var session = Snapshot();
        delivery.RegisterSession(session.Id, 123, -123, false);
        await Publish(delivery, session, new ApprovalRequestedEvent("upstream", AgentToolKind.Command, "a < b")
            { RequestId = "R000001", CanApproveForSession = true });
        await Eventually(() => api.Approvals.Count > 0);
        var approval = Assert.Single(api.Approvals);
        Assert.NotNull(approval.Keyboard);
        Assert.Contains("a < b", approval.Text);
        Assert.DoesNotContain("/approve", approval.Text);
        Assert.DoesNotContain("/deny", approval.Text);
        Assert.True(delivery.OwnsApprovalMessage("R000001", 123, -123, 42));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProseWithoutNewlinePrecedesMultilineCommandsAndLaterTextEvenAfterResend(bool failCommand)
    {
        var api = new FormattedApi { FailOnAttempt = failCommand ? 2 : 0 };
        var delivery = Delivery(api);
        var session = Snapshot();
        delivery.RegisterSession(session.Id, 123, -123, false);
        delivery.SetActiveSession(123, session.Id);
        await Publish(delivery, session, new MessageDeltaEvent("item", "Vou executar isto:"));
        await Publish(delivery, session, new ToolStartedEvent("cmd1", AgentToolKind.Command, "echo primeiro\necho segundo"));
        await Publish(delivery, session, new MessageDeltaEvent("item", "Depois do primeiro comando."));
        await Publish(delivery, session, new ToolStartedEvent("cmd2", AgentToolKind.Command, "echo terceiro\necho quarto"));
        await Publish(delivery, session, new MessageDeltaEvent("item", "Fim."));
        await Publish(delivery, session, new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => delivery.Get(session.Id, 123)?.State ==
            (failCommand ? TelegramDeliveryState.Failed : TelegramDeliveryState.Delivered));
        if (failCommand)
        {
            api.FailOnAttempt = 0;
            await delivery.RetryAsync(session.Id, 123, -123, default);
        }
        Assert.Equal(new[] { "Vou executar isto:", "→ Executando comando\necho primeiro\necho segundo\n",
            "Depois do primeiro comando.", "→ Executando comando\necho terceiro\necho quarto\n", "Fim." },
            api.Sent.Select(part => part.PlainText));
        Assert.All(api.Sent, TelegramMessageFormatterTests.AssertValid);
        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get(session.Id, 123)!.State);
    }

    [Fact]
    public async Task CommandBoundaryDoesNotSplitOrOvertakeUnicodeAcrossDeltas()
    {
        var api = new FormattedApi();
        var delivery = Delivery(api);
        var session = Snapshot();
        delivery.RegisterSession(session.Id, 123, -123, false);
        delivery.SetActiveSession(123, session.Id);
        var before = new string('x', 5000);
        await Publish(delivery, session, new MessageDeltaEvent("item", before + "\uD83D"));
        await Publish(delivery, session, new ToolStartedEvent("cmd", AgentToolKind.Command, "echo um\necho dois"));
        await Publish(delivery, session, new MessageDeltaEvent("item", "\uDE00Fim"));
        await Publish(delivery, session, new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => delivery.Get(session.Id, 123)?.State == TelegramDeliveryState.Delivered);
        Assert.Equal(before + "😀→ Executando comando\necho um\necho dois\nFim",
            string.Concat(api.Sent.Select(part => part.PlainText)));
        Assert.All(api.Sent, TelegramMessageFormatterTests.AssertValid);
    }

    private static TelegramDeliveryService Delivery(FormattedApi api) =>
        new(api, NullLogger<TelegramDeliveryService>.Instance) { PartInterval = TimeSpan.Zero };

    private static AgentSessionSnapshot Snapshot() => new("S000001", AgentKind.Codex, 123,
        JobExecutionContext.General("/tmp/general"), AgentPermissionProfile.Manual,
        AgentSessionState.Running, "T000001", 0, [], true, DateTimeOffset.UtcNow, null, null);

    private static Task Publish(TelegramDeliveryService delivery, AgentSessionSnapshot session, AgentEvent evt) =>
        delivery.PublishAsync(session, evt with { SessionId = session.Id, TurnId = "T000001" }, default);

    private static async Task Eventually(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class FormattedApi : ITelegramBotApi
    {
        public bool SupportsInlineKeyboards => true;
        public readonly ConcurrentQueue<TelegramFormattedMessage> Attempts = new();
        public readonly ConcurrentQueue<TelegramFormattedMessage> Sent = new();
        public readonly ConcurrentQueue<string> PlainSent = new();
        public readonly ConcurrentQueue<(string Text, TelegramInlineKeyboard? Keyboard)> Approvals = new();
        public int FailOnAttempt;
        public HttpStatusCode FailureStatus = HttpStatusCode.BadRequest;
        public bool RejectMarkup;
        public bool FailPlain;
        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            if (FailPlain) throw new HttpRequestException("falha", null, HttpStatusCode.BadRequest);
            PlainSent.Enqueue(text);
            return Task.CompletedTask;
        }
        public Task<long?> SendApprovalAsync(long chatId, string text, TelegramInlineKeyboard? keyboard,
            CancellationToken cancellationToken)
        {
            Approvals.Enqueue((text, keyboard));
            return Task.FromResult<long?>(42);
        }
        public Task<long?> SendFormattedMessageAsync(long chatId, TelegramFormattedMessage message,
            TelegramInlineKeyboard? keyboard, CancellationToken cancellationToken)
        {
            Attempts.Enqueue(message);
            if (RejectMarkup) throw new TelegramMarkupException();
            if (Attempts.Count == FailOnAttempt) throw new HttpRequestException("falha", null, FailureStatus);
            Sent.Enqueue(message);
            return Task.FromResult<long?>(42);
        }
    }
}
