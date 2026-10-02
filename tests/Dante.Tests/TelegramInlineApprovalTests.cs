using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramInlineApprovalTests
{
    [Theory]
    [InlineData(AgentApprovalDecision.ApproveOnce, false)]
    [InlineData(AgentApprovalDecision.ApproveForSession, true)]
    [InlineData(AgentApprovalDecision.Deny, false)]
    public async Task CallbackResolvesExactlyOnceAndRemovesKeyboard(AgentApprovalDecision decision, bool sessionApproval)
    {
        await using var fixture = await Fixture.Start();
        var message = await fixture.Approval(sessionApproval);
        var button = message.Keyboard!.Rows.SelectMany(row => row).Single(b =>
            b.CallbackData.EndsWith(":" + (int)decision));
        Assert.All(message.Keyboard.Rows.SelectMany(row => row), b => Assert.InRange(b.CallbackData.Length, 1, 64));
        Assert.Equal(sessionApproval, message.Keyboard.Rows.SelectMany(row => row).Any(b => b.Text == "Aprovar na sessão"));
        fixture.Api.Callback(button.CallbackData, message.Id);
        Assert.Equal(TelegramApprovalCallback.DecisionText(decision), await fixture.Api.NextAnswer());
        await Eventually(() => fixture.Api.Edits.Count > 0);
        Assert.Contains(TelegramApprovalCallback.DecisionText(decision), fixture.Api.Edits.Single());
        Assert.Equal(decision, Assert.IsType<AgentApprovalResponse>(Assert.Single(fixture.Driver.Responses).Response).Decision);
        fixture.Api.Callback(button.CallbackData, message.Id);
        Assert.Contains("indisponível", await fixture.Api.NextAnswer());
        Assert.Single(fixture.Driver.Responses);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("turn")]
    [InlineData("session")]
    [InlineData("request")]
    [InlineData("message")]
    [InlineData("chat")]
    [InlineData("capability")]
    [InlineData("malformed")]
    public async Task InvalidCallbacksNeverReachDriver(string invalid)
    {
        await using var fixture = await Fixture.Start();
        var message = await fixture.Approval(false);
        var data = message.Keyboard!.Rows[0][0].CallbackData;
        data = invalid switch
        {
            "turn" => data.Replace("T000001", "T999999"),
            "session" => data.Replace("S000001", "S999999"),
            "request" => data.Replace("R000001", "R999999"),
            "capability" => data[..data.LastIndexOf(':')] + ":" + (int)AgentApprovalDecision.ApproveForSession,
            "malformed" => "arbitrary data",
            _ => data
        };
        fixture.Api.Callback(data, invalid == "message" ? 999 : message.Id,
            invalid == "owner" ? 456 : 123, invalid == "chat" ? -456 : -123);
        Assert.Contains("indisponível", await fixture.Api.NextAnswer());
        Assert.Empty(fixture.Driver.Responses);
        Assert.Single(fixture.Sessions.GetActive(123)!.PendingRequestIds);
    }

    [Fact]
    public async Task ExpiryRemovesKeyboardAndLateCallbackIsRefused()
    {
        await using var fixture = await Fixture.Start(TimeSpan.FromSeconds(2));
        var message = await fixture.Approval(false);
        await Eventually(() => fixture.Api.Edits.Any(text => text.Contains("expirada")));
        Assert.Equal(AgentApprovalDecision.Deny,
            Assert.IsType<AgentApprovalResponse>(Assert.Single(fixture.Driver.Responses).Response).Decision);
        fixture.Api.Callback(message.Keyboard!.Rows[0][0].CallbackData, message.Id);
        Assert.Contains("indisponível", await fixture.Api.NextAnswer());
        Assert.Single(fixture.Driver.Responses);
    }

    [Theory]
    [InlineData("/approve", AgentApprovalDecision.ApproveOnce)]
    [InlineData("/approve-session", AgentApprovalDecision.ApproveForSession)]
    [InlineData("/deny", AgentApprovalDecision.Deny)]
    public async Task TextFallbackUpdatesTheOriginalApproval(string command, AgentApprovalDecision decision)
    {
        await using var fixture = await Fixture.Start();
        await fixture.Approval(true);
        fixture.Api.Text(command + " S000001 T000001 R000001");
        await Eventually(() => fixture.Api.Edits.Any(text => text.Contains(TelegramApprovalCallback.DecisionText(decision))));
        Assert.Equal(decision, Assert.IsType<AgentApprovalResponse>(Assert.Single(fixture.Driver.Responses).Response).Decision);
    }

    [Fact]
    public async Task BoundSecretsHideDetailsWhileAllowingApproval()
    {
        await using var fixture = await Fixture.Start(hideOutput: true);
        var message = await fixture.Approval(true);
        Assert.DoesNotContain("sensitive command", message.Text);
        Assert.DoesNotContain("sensitive reason", message.Text);
        fixture.Api.Callback(message.Keyboard!.Rows[0][0].CallbackData, message.Id);
        await fixture.Api.NextAnswer();
        await Eventually(() => fixture.Api.Edits.Count > 0);
        Assert.All(fixture.Api.Edits, text => Assert.DoesNotContain("sensitive", text));
        Assert.Single(fixture.Driver.Responses);
    }

    [Fact]
    public async Task ExpiryDuringSendLeavesNoActiveKeyboard()
    {
        await using var fixture = await Fixture.Start(TimeSpan.FromSeconds(1));
        fixture.Api.SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Driver.Emit(new ApprovalRequestedEvent("upstream", AgentToolKind.Command, "command"));
        await Eventually(() => fixture.Api.SendStarted);
        await Eventually(() => fixture.Driver.Responses.Count == 1);
        fixture.Api.SendGate.SetResult();
        await Eventually(() => fixture.Api.Edits.Any(text => text.Contains("expirada")));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly ApprovalApi Api = new();
        public readonly FakeSessionDriverFactory Drivers = new();
        public SessionRegistry Sessions = null!;
        public TelegramPollingService Polling = null!;
        public FakeSessionDriver Driver => Assert.Single(Drivers.Created);

        public static async Task<Fixture> Start(TimeSpan? timeout = null, bool hideOutput = false)
        {
            var fixture = new Fixture();
            var delivery = new TelegramDeliveryService(fixture.Api, NullLogger<TelegramDeliveryService>.Instance)
                { PartInterval = TimeSpan.Zero };
            fixture.Sessions = new SessionRegistry(fixture.Drivers, NullLogger<SessionRegistry>.Instance,
                delivery, requestTimeout: timeout);
            var started = await fixture.Sessions.StartAsync(new SessionStartRequest(123, AgentKind.Codex,
                JobExecutionContext.General("/tmp/general")));
            delivery.RegisterSession(started.Session!.Id, 123, -123, hideOutput);
            delivery.SetActiveSession(123, started.Session.Id);
            await fixture.Sessions.SubmitAsync(123, null, "tarefa");
            var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
            var runner = new UnusedRunner();
            fixture.Polling = new TelegramPollingService(fixture.Api, options, new TelegramUserAuthorizer(options),
                runner, runner, new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
                sessions: fixture.Sessions, delivery: delivery);
            await fixture.Polling.StartAsync(CancellationToken.None);
            return fixture;
        }

        public async Task<SentApproval> Approval(bool forSession)
        {
            Driver.Emit(new ApprovalRequestedEvent("upstream", AgentToolKind.Command, "sensitive command", "sensitive reason")
                { CanApproveForSession = forSession });
            var sent = await Api.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Eventually(() => Sessions.GetActive(123)!.PendingRequestIds.Count > 0);
            // SendMessage must have returned its id before a callback can be correlated.
            await Task.Delay(20);
            return sent;
        }

        public async ValueTask DisposeAsync()
        {
            await Polling.StopAsync(CancellationToken.None);
            Polling.Dispose();
            await Sessions.DisposeAsync();
        }
    }

    private sealed record SentApproval(long Id, string Text, TelegramInlineKeyboard? Keyboard);

    private sealed class ApprovalApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> answers = Channel.CreateUnbounded<string>();
        public readonly Channel<SentApproval> Sent = Channel.CreateUnbounded<SentApproval>();
        public readonly ConcurrentQueue<string> Edits = new();
        public TaskCompletionSource? SendGate;
        public bool SendStarted;
        private long sequence;
        public void Text(string text) => updates.Writer.TryWrite(new TelegramUpdate(Interlocked.Increment(ref sequence),
            new TelegramMessage(new TelegramChat(-123), text, new TelegramUser(123))));
        public void Callback(string data, long messageId, long userId = 123, long chatId = -123)
        {
            // Deserialize a realistic Telegram update, including fields unrelated to our domain.
            var json = JsonSerializer.Serialize(new { update_id = Interlocked.Increment(ref sequence), callback_query = new
                { id = "callback-id", from = new { id = userId, is_bot = false }, data,
                    message = new { message_id = messageId, chat = new { id = chatId, type = "private" }, text = "Approval" } } });
            updates.Writer.TryWrite(JsonSerializer.Deserialize<TelegramUpdate>(json)!);
        }
        public async Task<string> NextAnswer() => await answers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
            => [await updates.Reader.ReadAsync(cancellationToken)];
        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<long?> SendApprovalAsync(long chatId, string text, TelegramInlineKeyboard? keyboard,
            CancellationToken cancellationToken)
        {
            SendStarted = true;
            if (SendGate is { } hold) await hold.Task;
            Sent.Writer.TryWrite(new SentApproval(42, text, keyboard));
            return 42;
        }
        public Task EditApprovalAsync(long chatId, long messageId, string text, CancellationToken cancellationToken)
        {
            Assert.Equal(42, messageId);
            Edits.Enqueue(text);
            return Task.CompletedTask;
        }
        public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken cancellationToken)
        {
            Assert.Equal("callback-id", callbackId);
            answers.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Worker.Attachments.Attachment>? attachments = null)
            => throw new InvalidOperationException("Callbacks não iniciam jobs.");
    }
}
