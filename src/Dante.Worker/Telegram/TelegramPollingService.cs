using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using System.Text;
using Microsoft.Extensions.Options;

namespace Dante.Worker.Telegram;

public sealed class TelegramPollingService(
    ITelegramBotApi botApi,
    IOptions<TelegramOptions> options,
    TelegramUserAuthorizer authorizer,
    ICodexRunner codexRunner,
    IClaudeRunner claudeRunner,
    JobRegistry jobs,
    ILogger<TelegramPollingService> logger,
    RepositoryRegistry? repositories = null,
    GeneralWorkspace? generalWorkspace = null) : BackgroundService
{
    private const int MaxMessageLength = 4000;
    private readonly object runningGate = new();
    private readonly HashSet<Task> runningJobs = [];
    private readonly GeneralWorkspace generalWorkspace = generalWorkspace ?? new GeneralWorkspace();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Task[] pending;
        lock (runningGate)
        {
            pending = runningJobs.ToArray();
        }

        await Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BotToken))
        {
            logger.LogWarning("Telegram__BotToken não configurado; polling desativado.");
            return;
        }

        long offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await botApi.GetUpdatesAsync(offset, stoppingToken);
                foreach (var update in updates)
                {
                    // Advance before dispatch so a failed command is not executed again on the next poll.
                    offset = Math.Max(offset, update.UpdateId + 1);
                    if (update.Message is { Text: not null } message
                        && authorizer.IsAuthorized(message.From))
                    {
                        await HandleMessageAsync(message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Exception messages from HTTP clients can contain the token-bearing request URL.
                logger.LogWarning("Falha no polling do Telegram ({ErrorType}); tentando novamente.",
                    exception.GetType().Name);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleMessageAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        var text = message.Text!.Trim();
        if (string.Equals(text, "/ping", StringComparison.OrdinalIgnoreCase))
        {
            await botApi.SendMessageAsync(message.Chat.Id, "pong", cancellationToken);
            return;
        }

        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var command = separator < 0 ? text : text[..separator];
        var prompt = separator < 0 ? string.Empty : text[(separator + 1)..].Trim();
        if (string.Equals(command, "/repos", StringComparison.OrdinalIgnoreCase))
        {
            var registered = repositories?.List() ?? [];
            await SendLongMessageAsync(message.Chat.Id,
                registered.Count == 0 ? "Nenhum repositório cadastrado." :
                    string.Join('\n', registered.Select(FormatRepository)), cancellationToken);
            return;
        }

        if (string.Equals(command, "/repo", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRepositoryCommandAsync(message.Chat.Id, prompt, cancellationToken);
            return;
        }

        if (string.Equals(command, "/status", StringComparison.OrdinalIgnoreCase))
        {
            var visible = jobs.GetVisible();
            var response = visible.Count == 0
                ? "Nenhum job registrado."
                : string.Join('\n', visible.Select(FormatJob));
            await SendLongMessageAsync(message.Chat.Id, response, cancellationToken);
            return;
        }

        if (string.Equals(command, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            if (prompt.Length == 0 || prompt.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
            {
                await botApi.SendMessageAsync(message.Chat.Id, "Uso: /cancel <jobId>", cancellationToken);
                return;
            }

            var response = jobs.TryCancel(prompt, out var cancelled)
                ? $"Cancelamento solicitado para {cancelled!.Id}."
                : $"Job {prompt} não encontrado ou já encerrado.";
            await botApi.SendMessageAsync(message.Chat.Id, response, cancellationToken);
            return;
        }

        var isCodex = string.Equals(command, "/codex", StringComparison.OrdinalIgnoreCase);
        var isClaude = string.Equals(command, "/claude", StringComparison.OrdinalIgnoreCase);
        if (!isCodex && !isClaude)
        {
            return;
        }

        var agent = isCodex ? "Codex" : "Claude";
        if (prompt.Length == 0)
        {
            await botApi.SendMessageAsync(message.Chat.Id, $"Uso: /{agent.ToLowerInvariant()} <prompt>",
                cancellationToken);
            return;
        }

        if (repositories?.List().Any(repository =>
                IsWithin(generalWorkspace.Path, repository.Path) ||
                IsWithin(repository.Path, generalWorkspace.Path)) == true)
        {
            await botApi.SendMessageAsync(message.Chat.Id,
                "O workspace geral coincide com um repositório cadastrado; configure DANTE_GENERAL_WORKSPACE fora dos projetos.",
                cancellationToken);
            return;
        }

        var (job, jobToken) = jobs.Create(agent, cancellationToken);
        try
        {
            await botApi.SendMessageAsync(message.Chat.Id, $"{agent} iniciado. Job ID: {job.Id}.", cancellationToken);
        }
        catch
        {
            jobs.Complete(job.Id, AgentProcessStatus.Failed, errorMessage: "Falha ao confirmar o início.");
            throw;
        }

        var task = RunJobAsync(job.Id, agent, isCodex, prompt, message.Chat.Id, jobToken,
            cancellationToken);
        lock (runningGate)
        {
            runningJobs.Add(task);
        }

        _ = task.ContinueWith(completed =>
        {
            lock (runningGate)
            {
                runningJobs.Remove(completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleRepositoryCommandAsync(long chatId, string prompt, CancellationToken cancellationToken)
    {
        const string usage = "Uso: /repo add @alias <path> [owner/repo] | show @alias | remove @alias";
        var parts = SplitCommandArguments(prompt);
        if (parts is null)
        {
            await botApi.SendMessageAsync(chatId, "Aspas não fechadas no comando /repo.", cancellationToken);
            return;
        }
        if (repositories is null || parts.Length < 2)
        {
            await botApi.SendMessageAsync(chatId, usage, cancellationToken);
            return;
        }

        string response;
        try
        {
            if (parts[0].Equals("add", StringComparison.OrdinalIgnoreCase) && parts.Length is 3 or 4)
            {
                response = "Repositório cadastrado: " + FormatRepository(
                    repositories.Add(parts[1], parts[2], parts.Length == 4 ? parts[3] : null));
            }
            else if (parts[0].Equals("show", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
            {
                var found = repositories.Get(parts[1]);
                response = found is null ? "Repositório não cadastrado." : FormatRepository(found);
            }
            else if (parts[0].Equals("remove", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
            {
                response = repositories.Remove(parts[1]) ? "Repositório removido." : "Repositório não cadastrado.";
            }
            else response = usage;
        }
        catch (ArgumentException exception)
        {
            response = exception.Message;
        }
        catch (IOException)
        {
            response = "Não foi possível salvar o catálogo de repositórios.";
        }

        await botApi.SendMessageAsync(chatId, response, cancellationToken);
    }

    private static string FormatRepository(RepositoryDefinition repository) =>
        $"{repository.Alias}: {repository.Path}" +
        (repository.GitHub is null ? string.Empty : $" ({repository.GitHub})");

    private static string[]? SplitCommandArguments(string input)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var character in input)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (!started) continue;
                arguments.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(character);
                started = true;
            }
        }
        if (quoted) return null;
        if (started) arguments.Add(current.ToString());
        return arguments.ToArray();
    }

    private async Task RunJobAsync(string id, string agent, bool isCodex, string prompt, long chatId,
        CancellationToken jobToken, CancellationToken stoppingToken)
    {
        AgentProcessResult? result = null;
        var status = AgentProcessStatus.Cancelled;
        string? errorMessage = null;
        try
        {
            if (jobs.TryStart(id))
            {
                result = isCodex
                    ? await codexRunner.RunAsync(prompt, generalWorkspace.Path, jobToken, generalMode: true)
                    : await claudeRunner.RunAsync(prompt, generalWorkspace.Path, jobToken, generalMode: true);
                status = result.Status;
                errorMessage = result.ErrorMessage;
            }
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            status = AgentProcessStatus.Cancelled;
        }
        catch (Exception exception)
        {
            status = AgentProcessStatus.Failed;
            errorMessage = "Erro interno de execução.";
            logger.LogError("Falha inesperada no runner {Agent} ({ErrorType}).", agent,
                exception.GetType().Name);
        }

        var completed = jobs.Complete(id, status, result?.ExitCode, errorMessage);
        var response = completed.Status switch
        {
            JobStatus.Succeeded => $"{agent} concluído. Job {id}.\n\n{OutputOrFallback(result!.StandardOutput)}",
            JobStatus.Cancelled => $"{agent} cancelado. Job {id}.",
            _ => $"{agent} falhou. Job {id}.\n\n{(result is null ? errorMessage : FailureDetails(result))}"
        };

        try
        {
            await SendLongMessageAsync(chatId, response, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The worker is shutting down; the job state is already final.
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao informar o resultado do job {JobId} ({ErrorType}).", id,
                exception.GetType().Name);
        }
    }

    private static string FormatJob(JobSnapshot job) =>
        $"{job.Id} {job.Agent}: {job.Status}" +
        (job.CancellationRequested && job.Status is JobStatus.Queued or JobStatus.Running
            ? " (cancelamento solicitado)" : string.Empty) +
        $" | criado {job.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative));
    }

    private async Task SendLongMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(MaxMessageLength, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
            {
                length--;
            }

            await botApi.SendMessageAsync(chatId, text.Substring(start, length), cancellationToken);
            start += length;
        }
    }

    private static string OutputOrFallback(string output) =>
        string.IsNullOrWhiteSpace(output) ? "(sem saída)" : output;

    private static string FailureDetails(AgentProcessResult result)
    {
        var details = new[] { result.ErrorMessage, result.StandardError, result.StandardOutput }
            .Where(detail => !string.IsNullOrWhiteSpace(detail));
        return OutputOrFallback(string.Join("\n\n", details));
    }
}
