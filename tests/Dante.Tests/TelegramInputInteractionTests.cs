using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramInputInteractionTests
{
    [Theory]
    [InlineData("button")]
    [InlineData("reply")]
    [InlineData("command")]
    public async Task AnswerIsConsumedOnceWithoutANewTurnAndUpdatesTheQuestion(string route)
    {
        await using var fixture = await Fixture.Start();
        var sent = await fixture.Question([new("q", "Qual ferramenta?", ["Opção A", "Opção B"])]);
        Assert.DoesNotContain("S000001", sent.Text);
        Assert.DoesNotContain("T000001", sent.Text);
        Assert.DoesNotContain("R000001", sent.Text);
        Assert.DoesNotContain("/input", sent.Text);
        Assert.Equal(2, sent.Keyboard!.Rows.Count);
        var button = sent.Keyboard.Rows[1][0];
        Assert.DoesNotContain("Opção", button.CallbackData);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(button.CallbackData), 1, 64);
        if (route == "button")
        {
            fixture.Api.Callback(button.CallbackData, sent.Id);
            Assert.Equal("Resposta enviada.", await fixture.Api.NextAnswer());
        }
        else
        {
            if (route == "reply") fixture.Api.Reply(sent.Id, "orientação personalizada");
            else fixture.Api.Text("/input S000001 T000001 R000001 orientação personalizada");
            Assert.Contains(route == "reply" ? "Resposta enviada" : "Resposta entregue", await fixture.Api.NextText());
        }
        await Eventually(() => fixture.Api.Edits.Any(edit => edit.Text.Contains("Resposta enviada")));
        var response = Assert.IsType<AgentInputResponse>(Assert.Single(fixture.Driver.Responses).Response);
        Assert.Equal(route == "button" ? "Opção B" : "orientação personalizada", response.Answers["q"]);
        Assert.Single(fixture.Driver.Calls, call => call.StartsWith("turn:"));
        fixture.Api.Reply(sent.Id, "resposta duplicada");
        Assert.Contains("já foi respondida ou expirou", await fixture.Api.NextText());
        fixture.Api.Callback(button.CallbackData, sent.Id);
        Assert.Contains("disponível", await fixture.Api.NextAnswer());
        Assert.Single(fixture.Driver.Responses);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("chat")]
    [InlineData("message")]
    [InlineData("original-chat")]
    public async Task InvalidRepliesNeverReachTheDriverOrBecomeConversation(string invalid)
    {
        await using var fixture = await Fixture.Start();
        var sent = await fixture.Question([new("q", "Pergunta aberta", [])]);
        Assert.Null(sent.Keyboard);
        fixture.Api.Reply(invalid == "message" ? 9999 : sent.Id, "texto",
            invalid == "owner" ? 456 : 123, invalid == "chat" ? -456 : -123,
            invalid == "original-chat" ? -456 : null);
        Assert.Contains("solicitação", await fixture.Api.NextText());
        Assert.Empty(fixture.Driver.Responses);
        Assert.Single(fixture.Driver.Calls, call => call.StartsWith("turn:"));
        Assert.Single(fixture.Sessions.GetActive(123)!.PendingRequestIds);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("chat")]
    [InlineData("message")]
    [InlineData("session")]
    [InlineData("turn")]
    [InlineData("request")]
    [InlineData("option")]
    [InlineData("malformed")]
    [InlineData("unauthorized")]
    public async Task InvalidButtonsNeverReachTheDriver(string invalid)
    {
        await using var fixture = await Fixture.Start();
        var sent = await fixture.Question([new("q", "Pergunta", ["A", "B"])]);
        var data = sent.Keyboard!.Rows[0][0].CallbackData;
        data = invalid switch
        {
            "session" => data.Replace("S000001", "S999999"),
            "turn" => data.Replace("T000001", "T999999"),
            "request" => data.Replace("R000001", "R999999"),
            "option" => data[..data.LastIndexOf(':')] + ":9",
            "malformed" => "in:S000001:T000001:R000001:-1",
            _ => data
        };
        fixture.Api.Callback(data, invalid == "message" ? 9999 : sent.Id,
            invalid == "unauthorized" ? 999 : invalid == "owner" ? 456 : 123, invalid == "chat" ? -456 : -123);
        Assert.Contains("disponível", await fixture.Api.NextAnswer());
        Assert.Empty(fixture.Driver.Responses);
        Assert.Single(fixture.Sessions.GetActive(123)!.PendingRequestIds);
    }

    [Fact]
    public async Task MultipleQuestionsRequireOrderedAnswersAndMultipleRequestsStaySeparate()
    {
        await using var fixture = await Fixture.Start();
        var first = await fixture.Question([new("one", "Primeira?", ["A"]), new("two", "Segunda?", [])], "first");
        var second = await fixture.Question([new("third", "Outra decisão?", [])], "second");
        Assert.Null(first.Keyboard);
        Assert.Contains("separando as respostas por |", first.Text);
        fixture.Api.Reply(first.Id, "incompleta");
        Assert.Contains("2 resposta(s)", await fixture.Api.NextText());
        Assert.Empty(fixture.Driver.Responses);
        fixture.Api.Reply(second.Id, "resposta separada");
        await fixture.Api.NextText();
        Assert.Equal("second", Assert.Single(fixture.Driver.Responses).RequestId);
        Assert.Single(fixture.Sessions.GetActive(123)!.PendingRequestIds);
        fixture.Api.Reply(first.Id, "primeira | segunda");
        await fixture.Api.NextText();
        await Eventually(() => fixture.Driver.Responses.Count == 2);
        var answer = Assert.IsType<AgentInputResponse>(fixture.Driver.Responses.Last().Response);
        Assert.Equal("primeira", answer.Answers["one"]);
        Assert.Equal("segunda", answer.Answers["two"]);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("stop")]
    [InlineData("close")]
    [InlineData("completed")]
    public async Task EndedRequestsUpdateMessagesAndRefuseLateReplies(string end)
    {
        await using var fixture = await Fixture.Start(timeout: end == "expired" ? TimeSpan.FromSeconds(2) : null);
        var sent = await fixture.Question([new("q", "Pergunta", ["A"])]);
        if (end == "stop") await fixture.Sessions.InterruptAsync(123, null);
        if (end == "close") await fixture.Sessions.CloseAsync(123, null);
        if (end == "completed") fixture.Driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => fixture.Api.Edits.Any(edit => edit.Id == sent.Id &&
            edit.Text.Contains(end == "expired" ? "expirada" : "encerrada")));
        var count = fixture.Driver.Responses.Count;
        fixture.Api.Reply(sent.Id, "tardia");
        Assert.Contains("já foi respondida ou expirou", await fixture.Api.NextText());
        fixture.Api.Callback(sent.Keyboard!.Rows[0][0].CallbackData, sent.Id);
        Assert.Contains("disponível", await fixture.Api.NextAnswer());
        Assert.Equal(count, fixture.Driver.Responses.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProtectedQuestionsExposeNeitherQuestionsNorOptionsAndRemainAnswerable(bool interactive)
    {
        await using var fixture = await Fixture.Start(hide: true, interactive: interactive);
        var sent = await fixture.Question([new("q", "sensitive question", ["sensitive option"])]);
        Assert.DoesNotContain("sensitive", sent.Text);
        Assert.Null(sent.Keyboard);
        Assert.Contains("Perguntas omitidas", sent.Text);
        Assert.Equal(!interactive, sent.Text.Contains("/input S000001 T000001 R000001"));
        if (interactive) fixture.Api.Reply(sent.Id, "minha resposta");
        else fixture.Api.Text("/input S000001 T000001 R000001 minha resposta");
        Assert.Contains(interactive ? "Resposta enviada" : "Resposta entregue", await fixture.Api.NextText());
        Assert.Equal("minha resposta", Assert.IsType<AgentInputResponse>(Assert.Single(fixture.Driver.Responses).Response).Answers["q"]);
        if (interactive)
        {
            await Eventually(() => fixture.Api.Edits.Count > 0);
            Assert.All(fixture.Api.Edits, edit => Assert.DoesNotContain("sensitive", edit.Text));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LongOrNumerousOptionsUseTextAndReply(bool longOption)
    {
        await using var fixture = await Fixture.Start();
        var options = longOption ? new[] { new string('x', 65) } : Enumerable.Range(0, 11).Select(index => "Opção " + index).ToArray();
        var sent = await fixture.Question([new("q", "Escolha", options)]);
        Assert.Null(sent.Keyboard);
        Assert.Contains("Opções:", sent.Text);
        Assert.Contains(options.Last(), sent.Text);
        fixture.Api.Reply(sent.Id, "alternativa livre");
        await fixture.Api.NextText();
        Assert.Equal("alternativa livre", Assert.IsType<AgentInputResponse>(Assert.Single(fixture.Driver.Responses).Response).Answers["q"]);
    }

    [Fact]
    public async Task ReplyKeepsItsOriginalSessionEvenWhenAnotherSessionIsSelected()
    {
        await using var fixture = await Fixture.Start();
        var original = fixture.Driver;
        var sent = await fixture.Question([new("q", "Pergunta", [])]);
        await fixture.Sessions.StartAsync(new SessionStartRequest(123, AgentKind.Codex, JobExecutionContext.General("/tmp/other")));
        fixture.Api.Reply(sent.Id, "resposta da primeira");
        await fixture.Api.NextText();
        Assert.Single(original.Responses);
        Assert.Empty(fixture.Drivers.Created.Last().Responses);
    }

    [Fact]
    public async Task ExpiryDuringSendRemovesTheButtonsOnceTheMessageArrives()
    {
        await using var fixture = await Fixture.Start(timeout: TimeSpan.FromSeconds(1));
        fixture.Api.SendGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Driver.Emit(new UserInputRequestedEvent("upstream", [new("q", "Pergunta", ["A"])]));
        await Eventually(() => fixture.Api.SendStarted);
        await Eventually(() => fixture.Driver.Responses.Count == 1);
        fixture.Api.SendGate.SetResult();
        await Eventually(() => fixture.Api.Edits.Any(edit => edit.Text.Contains("expirada")));
        var sent = await fixture.Api.NextSent();
        fixture.Api.Callback(sent.Keyboard!.Rows[0][0].CallbackData, sent.Id);
        Assert.Contains("disponível", await fixture.Api.NextAnswer());
        Assert.Single(fixture.Driver.Responses);
    }

    [Fact]
    public async Task ExpiryBeforeDeliverySendsNoActiveButtonsOrRepeatedStatus()
    {
        await using var fixture = await Fixture.Start(timeout: TimeSpan.FromMilliseconds(100));
        var sent = await fixture.Question([new("q", "Pergunta", ["A"])]);
        Assert.Null(sent.Keyboard);
        Assert.Contains("Solicitação expirada", sent.Text);
        Assert.Single(sent.Text.Split('\n'), line => line.Contains("expirada"));
        Assert.Empty(fixture.Api.Edits);
        fixture.Api.Reply(sent.Id, "tardia");
        Assert.Contains("já foi respondida ou expirou", await fixture.Api.NextText());
        Assert.Single(fixture.Driver.Responses);
    }

    [Fact]
    public async Task HtmlFallbackAndResendPreserveCorrelationAndKeyboard()
    {
        await using var fixture = await Fixture.Start();
        fixture.Api.RejectMarkup = true;
        fixture.Api.FailSend = true;
        fixture.Driver.Emit(new UserInputRequestedEvent("upstream", [new("q", "Pergunta <texto>", ["A"])]));
        await Eventually(() => fixture.Delivery.Get("S000001", 123)?.State == TelegramDeliveryState.Failed);
        fixture.Api.FailSend = false;
        await fixture.Delivery.RetryAsync("S000001", 123, -123, default);
        var sent = await fixture.Api.NextSent();
        Assert.NotNull(sent.Keyboard);
        Assert.DoesNotContain("/input", sent.Text);
        fixture.Api.Reply(sent.Id, "resposta");
        await fixture.Api.NextText();
        Assert.Single(fixture.Driver.Responses);
    }

    [Fact]
    public async Task LongQuestionIsSplitAndEveryPartAcceptsReply()
    {
        await using var fixture = await Fixture.Start();
        var first = await fixture.Question([new("q", new string('x', 9000), [])]);
        await Eventually(() => fixture.Delivery.Get("S000001", 123)?.State == TelegramDeliveryState.Delivered);
        Assert.True(fixture.Api.SentCount > 1);
        Assert.All(fixture.Api.SentMessages, message => Assert.InRange(message.Text.Length, 1, 4000));
        fixture.Api.Reply(first.Id, "resposta longa");
        await fixture.Api.NextText();
        await Eventually(() => fixture.Api.Edits.Count == fixture.Api.SentCount);
        Assert.Single(fixture.Driver.Responses);
    }

    [Fact]
    public async Task PlainMessageIsNotGuessedAsAnAnswerAndUnauthorizedReplyIsIgnored()
    {
        await using var fixture = await Fixture.Start();
        var first = await fixture.Question([new("q", "Primeira pergunta", [])], "first");
        await fixture.Question([new("q", "Segunda pergunta", [])], "second");
        fixture.Api.Reply(first.Id, "não autorizado", user: 999);
        fixture.Api.Text("/ping");
        Assert.Equal("pong", await fixture.Api.NextText());
        Assert.Empty(fixture.Driver.Responses);
        fixture.Api.Text("uma mensagem comum");
        Assert.Contains("quando a resposta atual terminar", await fixture.Api.NextText());
        Assert.Empty(fixture.Driver.Responses);
        Assert.Equal(2, fixture.Sessions.GetActive(123)!.PendingRequestIds.Count);
        Assert.Equal(1, fixture.Sessions.GetActive(123)!.QueuedCount);
        fixture.Api.Reply(first.Id, "/mode auto é só texto da minha resposta");
        await fixture.Api.NextText();
        Assert.Equal("/mode auto é só texto da minha resposta",
            Assert.IsType<AgentInputResponse>(Assert.Single(fixture.Driver.Responses).Response).Answers["q"]);
    }

    [Fact]
    public async Task TransportWithoutInteractiveInputKeepsTextualFallback()
    {
        await using var fixture = await Fixture.Start(interactive: false);
        var sent = await fixture.Question([new("q", "Pergunta", ["A"])]);
        Assert.Contains("/input S000001 T000001 R000001", sent.Text);
        Assert.Null(sent.Keyboard);
        fixture.Api.Text("/input S000001 T000001 R000001 resposta");
        await fixture.Api.NextText();
        Assert.Single(fixture.Driver.Responses);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly InputApi Api = new();
        public readonly FakeSessionDriverFactory Drivers = new();
        public SessionRegistry Sessions = null!;
        public TelegramPollingService Polling = null!;
        public TelegramDeliveryService Delivery = null!;
        public FakeSessionDriver Driver => Drivers.Created[0];

        public static async Task<Fixture> Start(bool hide = false, bool interactive = true, TimeSpan? timeout = null)
        {
            var fixture = new Fixture();
            fixture.Api.SupportsInputMessages = interactive;
            fixture.Delivery = new TelegramDeliveryService(fixture.Api, NullLogger<TelegramDeliveryService>.Instance)
                { PartInterval = TimeSpan.Zero };
            fixture.Sessions = new SessionRegistry(fixture.Drivers, NullLogger<SessionRegistry>.Instance,
                fixture.Delivery, requestTimeout: timeout);
            var started = await fixture.Sessions.StartAsync(new SessionStartRequest(123, AgentKind.Codex,
                JobExecutionContext.General("/tmp/general")));
            fixture.Delivery.RegisterSession(started.Session!.Id, 123, -123, hide);
            fixture.Delivery.SetActiveSession(123, started.Session.Id);
            await fixture.Sessions.SubmitAsync(123, null, "tarefa");
            var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
            var runner = new UnusedRunner();
            fixture.Polling = new TelegramPollingService(fixture.Api, options, new TelegramUserAuthorizer(options),
                runner, runner, new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
                sessions: fixture.Sessions, delivery: fixture.Delivery);
            await fixture.Polling.StartAsync(default);
            return fixture;
        }

        public async Task<Sent> Question(IReadOnlyList<AgentQuestion> questions, string upstream = "upstream")
        {
            Driver.Emit(new UserInputRequestedEvent(upstream, questions));
            var sent = await Api.NextSent();
            await Eventually(() => Delivery.FindInputMessage(123, -123, sent.Id) is not null || !Api.SupportsInputMessages);
            return sent;
        }

        public async ValueTask DisposeAsync()
        {
            await Polling.StopAsync(default);
            Polling.Dispose();
            await Sessions.DisposeAsync();
        }
    }

    private sealed record Sent(long Id, string Text, TelegramInlineKeyboard? Keyboard);

    private sealed class InputApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<Sent> sent = Channel.CreateUnbounded<Sent>();
        private readonly Channel<string> texts = Channel.CreateUnbounded<string>();
        private readonly Channel<string> answers = Channel.CreateUnbounded<string>();
        public readonly ConcurrentQueue<(long Id, string Text)> Edits = new();
        public readonly ConcurrentQueue<Sent> SentMessages = new();
        public int SentCount => SentMessages.Count;
        public bool SupportsInputMessages { get; set; } = true;
        public bool RejectMarkup;
        public bool FailSend;
        public bool SendStarted;
        public TaskCompletionSource? SendGate;
        private long sequence;
        public void Text(string text) => updates.Writer.TryWrite(new(Interlocked.Increment(ref sequence),
            new(new(-123), text, new(123))));
        public void Reply(long messageId, string text, long user = 123, long chat = -123, long? originalChat = null)
        {
            var json = JsonSerializer.Serialize(new { update_id = Interlocked.Increment(ref sequence), message = new
                { chat = new { id = chat }, from = new { id = user }, text,
                    reply_to_message = new { message_id = messageId, chat = new { id = originalChat ?? chat } } } });
            updates.Writer.TryWrite(JsonSerializer.Deserialize<TelegramUpdate>(json)!);
        }
        public void Callback(string data, long messageId, long user = 123, long chat = -123) => updates.Writer.TryWrite(
            new(Interlocked.Increment(ref sequence), null, new("callback", new(user), new(new(chat), "Question", null, messageId), data)));
        public async Task<Sent> NextSent() => await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<string> NextText() => await texts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<string> NextAnswer() => await answers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken token)
            => [await updates.Reader.ReadAsync(token)];
        public Task SendMessageAsync(long chat, string text, CancellationToken token)
        {
            texts.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
        public async Task<long?> SendApprovalAsync(long chat, string text, TelegramInlineKeyboard? keyboard, CancellationToken token)
        {
            SendStarted = true;
            if (SendGate is { } hold) await hold.Task;
            if (FailSend) throw new HttpRequestException("rejected", null, HttpStatusCode.BadRequest);
            var message = new Sent(Interlocked.Increment(ref sequence), text, SupportsInputMessages ? keyboard : null);
            SentMessages.Enqueue(message);
            sent.Writer.TryWrite(message);
            return SupportsInputMessages ? message.Id : null;
        }
        public Task<long?> SendFormattedMessageAsync(long chat, TelegramFormattedMessage message,
            TelegramInlineKeyboard? keyboard, CancellationToken token)
        {
            if (RejectMarkup) throw new TelegramMarkupException();
            return SendApprovalAsync(chat, message.PlainText, keyboard, token);
        }
        public Task EditApprovalAsync(long chat, long id, string text, CancellationToken token)
        {
            Edits.Enqueue((id, text));
            return Task.CompletedTask;
        }
        public Task AnswerCallbackAsync(string id, string text, CancellationToken token)
        {
            answers.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null)
            => throw new InvalidOperationException("Input humano não inicia jobs.");
    }
}
