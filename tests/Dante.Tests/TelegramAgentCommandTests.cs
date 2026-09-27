using Dante.Worker.Agents;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramAgentCommandTests
{
    [Theory]
    [InlineData("/codex Responda CODEX OK", true)]
    [InlineData("/claude Responda CLAUDE OK", false)]
    public async Task AuthorizedCommandRunsSelectedAgentAndReturnsOutput(string command, bool useCodex)
    {
        var api = new CommandBotApi(command);
        var codex = new FakeRunner();
        var claude = new FakeRunner();
        using var service = CreateService(api, codex, claude);

        await RunUntilNextPollAsync(service, api);

        var selected = useCodex ? codex : claude;
        var other = useCodex ? claude : codex;
        Assert.Equal(1, selected.Calls);
        Assert.Equal("Responda " + (useCodex ? "CODEX OK" : "CLAUDE OK"), selected.Prompt);
        Assert.Equal("/tmp/agent-work", selected.WorkingDirectory);
        Assert.Equal(0, other.Calls);
        Assert.Equal(2, api.Messages.Count);
        Assert.EndsWith("iniciado.", api.Messages[0].Text);
        Assert.Contains("concluído.", api.Messages[1].Text);
        Assert.Contains("RESULT OK", api.Messages[1].Text);
    }

    [Theory]
    [InlineData("/codex")]
    [InlineData("/claude \n  ")]
    public async Task EmptyPromptReturnsUsageWithoutRunningAgent(string command)
    {
        var api = new CommandBotApi(command);
        var codex = new FakeRunner();
        var claude = new FakeRunner();
        using var service = CreateService(api, codex, claude);

        await RunUntilNextPollAsync(service, api);

        Assert.Equal(0, codex.Calls + claude.Calls);
        Assert.Single(api.Messages);
        Assert.StartsWith("Uso: ", api.Messages[0].Text);
    }

    [Fact]
    public async Task LongUnicodeOutputIsSplitWithoutBreakingSurrogatePairs()
    {
        var api = new CommandBotApi("/codex emoji");
        var codex = new FakeRunner { Output = string.Concat(Enumerable.Repeat("😀", 4500)) };
        using var service = CreateService(api, codex, new FakeRunner());

        await RunUntilNextPollAsync(service, api);

        Assert.True(api.Messages.Count > 2);
        Assert.All(api.Messages, message => Assert.InRange(message.Text.Length, 1, 4000));
        var combined = string.Concat(api.Messages.Skip(1).Select(message => message.Text));
        Assert.Equal("Codex concluído.\n\n" + codex.Output, combined);
        Assert.All(api.Messages.Skip(1), message =>
        {
            Assert.False(char.IsLowSurrogate(message.Text[0]));
            Assert.False(char.IsHighSurrogate(message.Text[^1]));
        });
    }

    [Fact]
    public async Task FailedAgentReportsDiagnosticsAndPollingContinues()
    {
        var api = new CommandBotApi("/claude fail");
        var claude = new FakeRunner
        {
            ResultStatus = AgentProcessStatus.Failed,
            Error = "exit code 1",
            StandardError = "agent diagnostic"
        };
        using var service = CreateService(api, new FakeRunner(), claude);

        await RunUntilNextPollAsync(service, api);

        Assert.Equal(1, claude.Calls);
        Assert.Contains("Claude falhou.", api.Messages[1].Text);
        Assert.Contains("agent diagnostic", api.Messages[1].Text);
        Assert.Equal(43, api.NextOffset);
    }

    [Fact]
    public async Task UnexpectedRunnerExceptionDoesNotRepeatCommand()
    {
        var api = new CommandBotApi("/codex fail");
        var codex = new FakeRunner { Throw = true };
        using var service = CreateService(api, codex, new FakeRunner());

        await RunUntilNextPollAsync(service, api);

        Assert.Equal(1, codex.Calls);
        Assert.Contains("Codex falhou", api.Messages[1].Text);
        Assert.Equal(43, api.NextOffset);
    }

    [Fact]
    public async Task UnauthorizedUserCannotRunAgent()
    {
        var api = new CommandBotApi("/codex secret", senderId: 999);
        var codex = new FakeRunner();
        using var service = CreateService(api, codex, new FakeRunner());

        await RunUntilNextPollAsync(service, api);

        Assert.Equal(0, codex.Calls);
        Assert.Empty(api.Messages);
    }

    private static TelegramPollingService CreateService(CommandBotApi api, FakeRunner codex, FakeRunner claude)
    {
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "test-token",
            AllowedUserIds = "123",
            AgentWorkingDirectory = "/tmp/agent-work"
        });
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), codex, claude,
            NullLogger<TelegramPollingService>.Instance);
    }

    private static async Task RunUntilNextPollAsync(TelegramPollingService service, CommandBotApi api)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            await api.NextPoll.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CommandBotApi(string command, long senderId = 123) : ITelegramBotApi
    {
        private int pollCount;
        public TaskCompletionSource NextPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(long ChatId, string Text)> Messages { get; } = [];
        public long NextOffset { get; private set; }

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref pollCount) == 1)
            {
                return [new TelegramUpdate(42,
                    new TelegramMessage(new TelegramChat(-123), command, new TelegramUser(senderId)))];
            }

            NextOffset = offset;
            NextPoll.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Messages.Add((chatId, text));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner : ICodexRunner, IClaudeRunner
    {
        public int Calls { get; private set; }
        public string? Prompt { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public string Output { get; set; } = "RESULT OK";
        public string StandardError { get; set; } = "";
        public string? Error { get; set; }
        public AgentProcessStatus ResultStatus { get; set; } = AgentProcessStatus.Succeeded;
        public bool Throw { get; set; }

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Prompt = prompt;
            WorkingDirectory = workingDirectory;
            if (Throw) throw new InvalidOperationException("sensitive detail");
            return Task.FromResult(new AgentProcessResult(ResultStatus, Output, StandardError, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Error));
        }
    }
}
