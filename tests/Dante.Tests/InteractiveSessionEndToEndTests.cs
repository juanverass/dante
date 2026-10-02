using System.Text.RegularExpressions;
using System.Threading.Channels;
using Dante.ProcessProbe;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// End to end through every real component of the interactive path (#68): Telegram polling → SessionRegistry → the real
// Claude/Codex drivers → InteractiveAgentProcessLauncher → FakeClaude/FakeCodex child processes speaking the real
// protocols → TelegramDeliveryService. Only the Telegram HTTP API and the CLIs themselves are simulated.
public sealed class InteractiveSessionEndToEndTests : IAsyncDisposable
{
    private const long Owner = 123;
    private const long OtherUser = 456;
    private static readonly Regex RequestIds = new(@"pendente (S\d+ T\d+ R\d+)");
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly BotApi api = new();
    private readonly ProbeLauncher launcher = new();
    private readonly List<SessionRegistry> registries = [];

    [Fact]
    public async Task ClaudeConversationStreamsKeepsContextAndHandlesSupervisionStopCloseAndCrash()
    {
        var host = await StartHostAsync(AgentKind.Claude);
        try
        {
            var first = await SendAsync("pong", "pong 1");
            Assert.Equal("pong 1\n", first);
            // Same process, same upstream session: the fake counts turns per process.
            await SendAsync("pong", "pong 2");

            api.Enqueue("write");
            var ids = Ids(await api.NextMessageContainingAsync("Aprovação pendente"));
            api.Enqueue("/approve " + ids, OtherUser);
            Assert.Equal("Solicitação não encontrada neste turno da sessão.",
                await api.NextMessageContainingAsync("Solicitação"));
            api.Enqueue("/approve-session " + ids);
            await api.NextMessageContainingAsync("Resposta entregue");
            await api.NextMessageContainingAsync("allow:session");

            api.Enqueue("write");
            ids = Ids(await api.NextMessageContainingAsync("Aprovação pendente"));
            api.Enqueue($"/deny {ids} fora do escopo");
            await api.NextMessageContainingAsync("deny:fora do escopo");

            api.Enqueue("ask");
            var question = await api.NextMessageContainingAsync("Resposta pendente");
            Assert.Contains("Which color? (opções: red, blue)", question);
            api.Enqueue($"/input {Ids(question)} blue");
            Assert.Contains("\"Which color?\":\"blue\"", await api.NextMessageContainingAsync("answers:"));

            api.Enqueue("slow");
            await api.NextMessageContainingAsync("working");
            api.Enqueue("mensagem durante o turno");
            await api.NextMessageContainingAsync("Recebido");
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageContainingAsync("pong"));
            api.Enqueue("/session stop");
            Assert.Contains("1 mensagem(ns) removida(s)", await api.NextMessageContainingAsync("Interrupção solicitada"));
            await api.NextMessageContainingAsync("Resposta interrompida.");

            // The interrupted turn kept the session and its process.
            await SendAsync("pong", "pong 7");
            api.Enqueue("/status");
            var status = await api.NextMessageContainingAsync("Sessões:");
            Assert.Contains("S000001 Claude General: Idle (ativa)", status);
            Assert.Contains("Entregas Telegram:", status);

            api.Enqueue("/session close");
            await api.NextMessageContainingAsync("Sessão S000001 encerrada.");
            await WaitUntilExitedAsync(launcher.Started[0]);

            // After close, the next message opens a new session (a new process: turns start over).
            await SendAsync("pong", "pong 1");
            Assert.Equal(2, launcher.Started.Count);
            api.Enqueue("crash");
            Assert.Contains("A sessão S000002 foi encerrada: O processo do Claude encerrou inesperadamente (código 5).",
                await api.NextMessageContainingAsync("foi encerrada"));
            api.Enqueue("pong");
            Assert.Equal("A sessão S000002 foi encerrada. Envie /session start para começar outra conversa.",
                await api.NextMessageContainingAsync("foi encerrada"));
            Assert.Equal(2, launcher.Started.Count);

            Assert.DoesNotContain(api.Sent, text => text.Contains("Job") || text.Contains("Turno") ||
                text.StartsWith("[S", StringComparison.Ordinal));
            Assert.Equal(0, host.Runner.Calls);
            Assert.True(api.ChatActions > 0);
        }
        finally { await host.Service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CodexConversationStreamsKeepsContextAndHandlesSupervisionSteerAndStop()
    {
        var host = await StartHostAsync(AgentKind.Codex);
        try
        {
            Assert.Equal("pong 1\n", await SendAsync("pong", "pong 1"));
            await SendAsync("pong", "pong 2");

            api.Enqueue("command");
            var approval = await api.NextMessageContainingAsync("Aprovação pendente");
            Assert.Contains("dotnet test", approval);
            Assert.Contains("Motivo: rodar os testes", approval);
            api.Enqueue("/approve " + Ids(approval));
            await api.NextMessageContainingAsync("decision:accept");

            api.Enqueue("command");
            api.Enqueue("/deny " + Ids(await api.NextMessageContainingAsync("Aprovação pendente")));
            await api.NextMessageContainingAsync("decision:decline");

            api.Enqueue("ask");
            var question = await api.NextMessageContainingAsync("Resposta pendente");
            Assert.Contains("Qual o codinome? (opções: alpha, beta)", question);
            api.Enqueue($"/input {Ids(question)} beta");
            Assert.Contains("\"codename\":{\"answers\":[\"beta\"]}", await api.NextMessageContainingAsync("answers:"));

            api.Enqueue("slow");
            await api.NextMessageContainingAsync("working");
            api.Enqueue("/steer foque nos testes");
            await api.NextMessageContainingAsync("Orientação enviada");
            await api.NextMessageContainingAsync("steered:foque nos testes");

            api.Enqueue("slow");
            await api.NextMessageContainingAsync("working");
            api.Enqueue("/session stop");
            await api.NextMessageContainingAsync("Resposta interrompida.");

            api.Enqueue("fail");
            await api.NextMessageContainingAsync("A resposta falhou: boom");
            await SendAsync("pong", "pong 9");
            Assert.Single(launcher.Started);

            api.Enqueue("/session close");
            await api.NextMessageContainingAsync("Sessão S000001 encerrada.");
            await WaitUntilExitedAsync(launcher.Started[0]);
            Assert.DoesNotContain(api.Sent, text => text.Contains("Job") || text.Contains("Turno"));
            Assert.Equal(0, host.Runner.Calls);
        }
        finally { await host.Service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task WorkerRestartInvalidatesOldSessionsAndTheirRequests()
    {
        var before = await StartHostAsync(AgentKind.Codex);
        await SendAsync("pong", "pong 1");
        api.Enqueue("command");
        var ids = Ids(await api.NextMessageContainingAsync("Aprovação pendente"));
        await before.Service.StopAsync(CancellationToken.None);
        before.Service.Dispose();
        await before.Sessions.DisposeAsync();
        await WaitUntilExitedAsync(launcher.Started[0]);

        var after = await StartHostAsync(AgentKind.Codex);
        try
        {
            api.Enqueue("/approve " + ids);
            Assert.Equal("Solicitação não encontrada neste turno da sessão.",
                await api.NextMessageContainingAsync("Solicitação"));
            api.Enqueue("/session select S000001");
            Assert.Contains("Sessões existem só em memória e não sobrevivem ao reinício do Worker.",
                await api.NextMessageContainingAsync("não encontrada"));
            // A plain message simply starts a new conversation in a new process.
            await SendAsync("pong", "pong 1");
            Assert.Equal(2, launcher.Started.Count);
        }
        finally { await after.Service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ExplicitOneShotCommandStillRunsAsAJobBesideTheConversation()
    {
        var host = await StartHostAsync(AgentKind.Claude);
        try
        {
            await SendAsync("pong", "pong 1");
            api.Enqueue("/codex resuma o repositório");
            Assert.Contains("Codex iniciado. Job ID: J000001 (General).", await api.NextMessageContainingAsync("Job ID"));
            Assert.Contains("Codex concluído. Job J000001 (General).\n\nresultado one-shot",
                await api.NextMessageContainingAsync("concluído"));
            Assert.Equal(1, host.Runner.Calls);
            await SendAsync("pong", "pong 2");
            Assert.Single(launcher.Started);
        }
        finally { await host.Service.StopAsync(CancellationToken.None); }
    }

    private async Task<string> SendAsync(string text, string expected)
    {
        api.Enqueue(text);
        return await api.NextMessageContainingAsync(expected);
    }

    private static string Ids(string request) => RequestIds.Match(request) is { Success: true } match
        ? match.Groups[1].Value
        : throw new Xunit.Sdk.XunitException("Solicitação sem ids: " + request);

    private async Task<(TelegramPollingService Service, SessionRegistry Sessions, OneShotRunner Runner)> StartHostAsync(
        AgentKind defaultAgent)
    {
        var settings = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        settings.SetDefaultAgent(defaultAgent);
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var sessions = new SessionRegistry(new AgentSessionDriverFactory(launcher), NullLogger<SessionRegistry>.Instance,
            delivery);
        registries.Add(sessions);
        var runner = new OneShotRunner();
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = $"{Owner},{OtherUser}" });
        var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            generalWorkspace: new GeneralWorkspace(Path.Combine(root, "general")), settings: settings,
            sessions: sessions, delivery: delivery);
        await service.StartAsync(CancellationToken.None);
        return (service, sessions, runner);
    }

    private static async Task WaitUntilExitedAsync(InteractiveAgentProcess process) =>
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(20));

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in registries) await registry.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    // Runs the fake CLI that speaks the requested agent's protocol through the real interactive launcher.
    private sealed class ProbeLauncher : IInteractiveAgentProcessLauncher
    {
        private static readonly string ProbeAssembly = typeof(ProbeMarker).Assembly.Location;
        private static readonly string RuntimeConfig =
            Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
        private readonly InteractiveAgentProcessLauncher inner = new(new DotnetResolver());
        private readonly List<InteractiveAgentProcess> started = [];

        public IReadOnlyList<InteractiveAgentProcess> Started
        {
            get { lock (started) return started.ToArray(); }
        }

        public async Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request,
            Func<string, string>? redactOutput = null, CancellationToken cancellationToken = default)
        {
            var fake = request.Agent == AgentKind.Codex ? "fake-codex" : "fake-claude";
            var process = await inner.StartAsync(request with
            {
                Arguments = ["exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly, fake, .. request.Arguments]
            }, redactOutput, cancellationToken);
            lock (started) started.Add(process);
            return process;
        }
    }

    private sealed class DotnetResolver : IAgentExecutableResolver
    {
        public string? Resolve(AgentKind agent)
        {
            var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            return (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, name))
                .FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException("dotnet host must be on PATH to run process tests.");
        }
    }

    private sealed class OneShotRunner : ICodexRunner, IClaudeRunner
    {
        private int calls;
        public int Calls => calls;

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Worker.Attachments.Attachment>? attachments = null)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "resultado one-shot", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private readonly List<string> sent = [];
        private long nextId;
        private int chatActions;

        public int ChatActions => chatActions;

        public IReadOnlyList<string> Sent
        {
            get { lock (sent) return sent.ToArray(); }
        }

        public void Enqueue(string text, long senderId = Owner) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(-100), text,
                new TelegramUser(senderId))));

        // Replies and streamed output interleave; skips whatever arrives before the expected text.
        public async Task<string> NextMessageContainingAsync(string expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                var message = await messages.Reader.ReadAsync().AsTask().WaitAsync(deadline - DateTime.UtcNow);
                if (message.Contains(expected, StringComparison.Ordinal)) return message;
            }
        }

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            lock (sent) sent.Add(text);
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref chatActions);
            return Task.CompletedTask;
        }
    }
}
