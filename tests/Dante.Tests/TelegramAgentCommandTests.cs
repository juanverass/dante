using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using Dante.Worker.Telegram;
using System.Diagnostics;
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
        Assert.Equal(new GeneralWorkspace().Path, selected.WorkingDirectory);
        Assert.True(selected.GeneralMode);
        Assert.Equal(0, other.Calls);
        Assert.Equal(2, api.Messages.Count);
        Assert.Contains("iniciado. Job ID: J", api.Messages[0].Text);
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
        Assert.StartsWith("Codex concluído. Job J", combined);
        Assert.EndsWith("\n\n" + codex.Output, combined);
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

    [Fact]
    public async Task DoesNotReturnInheritedAuthenticationSecretThroughTelegram()
    {
        const string name = "ANTHROPIC_API_KEY";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "dante-test-auth-secret");
        try
        {
            var api = new CommandBotApi("/claude question");
            var claude = new FakeRunner { Output = "dante-test-auth-secret" };
            using var service = CreateService(api, new FakeRunner(), claude);
            await RunUntilNextPollAsync(service, api);
            Assert.Contains("[segredo omitido]", api.Messages[1].Text);
            Assert.DoesNotContain("dante-test-auth-secret", api.Messages[1].Text);
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    [Theory]
    [InlineData(true, "/codex")]
    [InlineData(false, "/codex")]
    [InlineData(true, "/claude")]
    [InlineData(false, "/claude")]
    public async Task GeneralModeRejectsWorkspaceOverlapInBothDirections(bool generalInsideRepository,
        string command)
    {
        var root = Path.Combine(Path.GetTempPath(), "dante-overlap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repositoryPath = generalInsideRepository
                ? Path.Combine(root, "repository") : Path.Combine(root, "general", "repository");
            var generalPath = generalInsideRepository
                ? Path.Combine(repositoryPath, "general") : Path.Combine(root, "general");
            Directory.CreateDirectory(repositoryPath);
            using (var process = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryPath, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "init" }
            })!)
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }

            var registry = new RepositoryRegistry(Path.Combine(root, "repositories.json"));
            registry.Add("@project", repositoryPath);
            var general = new GeneralWorkspace(generalPath);
            var api = new CommandBotApi(command + " question");
            var codex = new FakeRunner();
            var claude = new FakeRunner();
            var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
            using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
                codex, claude, new JobRegistry(), NullLogger<TelegramPollingService>.Instance, registry, general);

            await RunUntilNextPollAsync(service, api);

            Assert.Equal(0, codex.Calls + claude.Calls);
            Assert.Single(api.Messages);
            Assert.Contains("coincide com um repositório", api.Messages[0].Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
            new JobRegistry(),
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
        public bool GeneralMode { get; private set; }
        public string Output { get; set; } = "RESULT OK";
        public string StandardError { get; set; } = "";
        public string? Error { get; set; }
        public AgentProcessStatus ResultStatus { get; set; } = AgentProcessStatus.Succeeded;
        public bool Throw { get; set; }

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null)
        {
            Calls++;
            Prompt = prompt;
            WorkingDirectory = workingDirectory;
            GeneralMode = generalMode;
            if (Throw) throw new InvalidOperationException("sensitive detail");
            return Task.FromResult(new AgentProcessResult(ResultStatus, Output, StandardError, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Error));
        }
    }
}
