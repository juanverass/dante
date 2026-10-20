using System.Diagnostics;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramRepositoryCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-repo-commands-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AuthorizedUserCanAddShowListAndRemove()
    {
        var repository = CreateRepository();
        var api = new BotApi();
        using var service = CreateService(api);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue($"/repo add @Demo {repository} owner/demo");
            Assert.Contains("@demo", await api.NextMessageAsync());
            api.Enqueue("/repo show @DEMO");
            Assert.Contains(repository, await api.NextMessageAsync());
            api.Enqueue("/repos");
            Assert.Contains("owner/demo", await api.NextMessageAsync());
            api.Enqueue($"/repo add @demo {repository}");
            Assert.Contains("já está cadastrado", await api.NextMessageAsync());
            api.Enqueue("/repo remove @demo");
            Assert.Contains("removido", await api.NextMessageAsync());
            api.Enqueue("/repo show @demo");
            Assert.Contains("não cadastrado", await api.NextMessageAsync());
            api.Enqueue("/repos");
            Assert.Contains("Nenhum", await api.NextMessageAsync());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RejectsInvalidInputAndUnauthorizedUser()
    {
        var repository = CreateRepository();
        var api = new BotApi();
        using var service = CreateService(api);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/repo add @bad-name " + repository);
            Assert.Contains("Alias inválido", await api.NextMessageAsync());
            api.Enqueue("/repo add @demo relative/path");
            Assert.Contains("absoluto", await api.NextMessageAsync());
            api.Enqueue("/repo add @demo " + Path.Combine(root, "missing"));
            Assert.Contains("não existe", await api.NextMessageAsync());
            api.Enqueue("/repo remove @missing");
            Assert.Contains("não cadastrado", await api.NextMessageAsync());
            api.Enqueue($"/repo add @hidden {repository}", 999);
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            api.Enqueue("/repos");
            Assert.Contains("Nenhum", await api.NextMessageAsync());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AcceptsQuotedRepositoryPathsWithSpaces()
    {
        var repository = CreateRepository("project with spaces");
        var api = new BotApi();
        using var service = CreateService(api);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue($"/repo add @spaced \"{repository}\" owner/demo");
            Assert.Contains("@spaced", await api.NextMessageAsync());
            api.Enqueue("/repo show @spaced");
            Assert.Contains(repository, await api.NextMessageAsync());
            api.Enqueue($"/repo add @bad \"{repository}");
            Assert.Contains("Aspas não fechadas", await api.NextMessageAsync());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task EnvironmentCommandsMaskBindingsAndDoNotReturnAgentSecrets()
    {
        var repository = CreateRepository();
        var registry = new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json"));
        registry.Add("@demo", repository);
        var api = new BotApi();
        var runner = new UnusedRunner();
        using var service = CreateService(api, registry, runner);
        var previous = Environment.GetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET");
        Environment.SetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET", "secret-value-123");
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/repo env set @demo API_BASE_URL https://example.test");
            Assert.DoesNotContain("https://example.test", await api.NextMessageAsync());
            api.Enqueue("/repo env bind @demo DATABASE_PASSWORD DANTE_TEST_TELEGRAM_SECRET");
            Assert.Contains("vinculada", await api.NextMessageAsync());
            api.Enqueue("/repo env list @demo");
            var list = await api.NextMessageAsync();
            Assert.Contains("DATABASE_PASSWORD (host: DANTE_TEST_TELEGRAM_SECRET)", list);
            Assert.DoesNotContain("secret-value-123", list);
            Assert.DoesNotContain("https://example.test", list);

            Environment.SetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET", null);
            api.Enqueue("/codex @demo run");
            Assert.Contains("não está configurada", await api.NextMessageAsync());
            Assert.Null(runner.Environment);
            Environment.SetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET", "secret-value-123");

            api.Enqueue("/codex @demo run");
            Assert.Contains("iniciado", await api.NextMessageAsync());
            var finished = await api.NextMessageAsync();
            Assert.Contains("Saída omitida", finished);
            Assert.DoesNotContain("secret-value-123", finished);
            Assert.Equal("secret-value-123", runner.Environment!["DATABASE_PASSWORD"]);

            api.Enqueue("/repo env remove @demo DATABASE_PASSWORD");
            Assert.Contains("removida", await api.NextMessageAsync());
            api.Enqueue("/repo env list @demo");
            Assert.DoesNotContain("DATABASE_PASSWORD", await api.NextMessageAsync());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("DANTE_TEST_TELEGRAM_SECRET", previous);
        }
    }

    // #232 (AD-58): link, list and unlink only between registered repositories, shown in /repos and /repo links.
    [Fact]
    public async Task LinkCommandsManageDirectionalLinks()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json"));
        registry.Add("@front", CreateRepository("front"));
        registry.Add("@back", CreateRepository("back"));
        var api = new BotApi();
        using var service = CreateService(api, registry);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/repo link @Front @BACK");
            var created = await api.NextMessageAsync();
            Assert.Contains("Vínculo criado: execuções em @front também acessam @back.", created);
            Assert.Contains("os que já estão em andamento mantêm o contexto", created);
            api.Enqueue("/repo link @front @back");
            Assert.Contains("@back já está vinculado a @front.", await api.NextMessageAsync());
            api.Enqueue("/repo link @front @front");
            Assert.Contains("ele mesmo", await api.NextMessageAsync());
            api.Enqueue("/repo link @front @ghost");
            Assert.Contains("@ghost não cadastrado", await api.NextMessageAsync());
            api.Enqueue("/repo links @front");
            Assert.Equal($"Vinculados a @front:\n- @back: {registry.Get("@back")!.Path}", await api.NextMessageAsync());
            api.Enqueue("/repo links @back");
            Assert.Equal("@back não tem repositórios vinculados.", await api.NextMessageAsync());
            api.Enqueue("/repos");
            Assert.Contains("| vinculados: @back", await api.NextMessageAsync());
            api.Enqueue("/repo link @front");
            Assert.StartsWith("Uso: /repo add", await api.NextMessageAsync());

            api.Enqueue("/repo unlink @front @back");
            Assert.Equal("Vínculo removido: jobs e sessões novos em @front não acessam mais @back.\n" +
                "Nenhum job ou sessão em andamento acessa @back.", await api.NextMessageAsync());
            api.Enqueue("/repo unlink @front @back");
            Assert.Equal("@back não está vinculado a @front.", await api.NextMessageAsync());
            Assert.Empty(new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json")).Get("@front")!.Links!);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    // #232: a job in the primary reaches the linked checkout with the contract, only with the primary's environment, and
    // reports both in its messages and in /status. Removing the linked repository removes the link with it.
    [Fact]
    public async Task JobsInALinkedRepositoryGetTheCheckoutAndTheContract()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json"));
        var front = CreateRepository("front");
        var back = CreateRepository("back");
        registry.Add("@front", front);
        registry.Add("@back", back);
        registry.SetLiteral("@front", "APP_ENV", "front");
        registry.Bind("@back", "API_TOKEN", "DANTE_TEST_LINKED_MISSING_" + Guid.NewGuid().ToString("N"));
        registry.Link("@front", "@back");
        var api = new BotApi();
        var runner = new UnusedRunner();
        using var service = CreateService(api, registry, runner);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/codex @front crie o endpoint e o cliente");
            Assert.StartsWith("Codex iniciado. Job ID: J000001 (@front + @back)", await api.NextMessageAsync());
            // The report of each checkout comes right after the header, before the agent's output.
            var branch = CurrentBranch(front);
            Assert.StartsWith("Codex concluído. Job J000001 (@front + @back).\nEstado dos repositórios:\n" +
                $"- @front: branch {branch}, sem alterações locais, sem upstream (branch não enviada)\n" +
                $"- @back: branch {branch}, sem alterações locais, sem upstream (branch não enviada)\n\n",
                await api.NextMessageAsync());
            Assert.Equal((front, back), (runner.WorkingDirectory, Assert.Single(runner.AdditionalDirectories!)));
            Assert.Equal(new Dictionary<string, string> { ["APP_ENV"] = "front" }, runner.Environment);
            Assert.StartsWith("Contexto multirrepositório escolhido pelo usuário", runner.Prompt);
            Assert.Contains($"- principal: @front em {front} (diretório de trabalho)\n- vinculado: @back em {back}", runner.Prompt);
            Assert.Contains("abra o PR no remoto de cada um", runner.Prompt);
            Assert.EndsWith("\nPedido do usuário:\ncrie o endpoint e o cliente", runner.Prompt);
            api.Enqueue("/status");
            Assert.Contains("J000001 Codex @front + @back: Succeeded", await api.NextMessageAsync());

            // Without links the prompt goes as typed.
            api.Enqueue("/codex @back revise");
            Assert.Contains("não está configurada", await api.NextMessageAsync());
            api.Enqueue("/repo remove @back");
            Assert.Equal("Repositório removido. Vínculos removidos junto: @front → @back.\n" +
                "Nenhum job ou sessão em andamento acessa @back.", await api.NextMessageAsync());
            api.Enqueue("/claude @front revise");
            Assert.StartsWith("Claude iniciado. Job ID: J000002 (@front)", await api.NextMessageAsync());
            Assert.DoesNotContain("Estado dos repositórios", await api.NextMessageAsync());
            Assert.Equal(("revise", 0), (runner.Prompt, runner.AdditionalDirectories!.Count));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    // A linked checkout that disappeared blocks the execution; the agent never runs with fewer repositories.
    [Fact]
    public async Task BrokenLinkBlocksTheJobWithGuidance()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json"));
        registry.Add("@front", CreateRepository("front"));
        var back = CreateRepository("back");
        registry.Add("@back", back);
        registry.Link("@front", "@back");
        Directory.Delete(back, recursive: true);
        var api = new BotApi();
        var runner = new UnusedRunner();
        using var service = CreateService(api, registry, runner);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/codex @front crie o endpoint");
            Assert.Equal("O diretório do repositório vinculado @back não existe mais. Corrija o cadastro ou use " +
                "/repo unlink @front @back.", await api.NextMessageAsync());
            api.Enqueue("/status");
            Assert.DoesNotContain("J000001", await api.NextMessageAsync());
            Assert.Null(runner.Prompt);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private TelegramPollingService CreateService(BotApi api, RepositoryRegistry? registry = null, UnusedRunner? runner = null)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        runner ??= new UnusedRunner();
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            registry ?? new RepositoryRegistry(Path.Combine(root, "catalog", "repositories.json")),
            checkouts: new LeitorDeCheckoutsGit());
    }

    private static string CurrentBranch(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("git")
        {
            WorkingDirectory = path, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, ArgumentList = { "branch", "--show-current" }
        })!;
        var branch = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return branch;
    }

    private string CreateRepository(string name = "project")
    {
        var path = Path.Combine(root, name);
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
        File.AppendAllText(Path.Combine(path, ".git", "config"),
            "\n[remote \"origin\"]\n\turl = https://github.com/owner/demo.git\n");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public void Enqueue(string command, long sender = 123) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(1), command, new TelegramUser(sender))));

        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedRunner : ICodexRunner, IClaudeRunner
    {
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }
        public string? Prompt { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public IReadOnlyList<string>? AdditionalDirectories { get; private set; }
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null, IReadOnlyList<string>? additionalDirectories = null)
        {
            Environment = environment;
            (Prompt, WorkingDirectory, AdditionalDirectories) = (prompt, workingDirectory, additionalDirectories);
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "secret-value-123", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }
}
