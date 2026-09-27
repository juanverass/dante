using Dante.Worker.Agents;
using Microsoft.Extensions.Options;

namespace Dante.Worker.Telegram;

public sealed class TelegramPollingService(
    ITelegramBotApi botApi,
    IOptions<TelegramOptions> options,
    TelegramUserAuthorizer authorizer,
    ICodexRunner codexRunner,
    IClaudeRunner claudeRunner,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    private const int MaxMessageLength = 4000;

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

        await botApi.SendMessageAsync(message.Chat.Id, $"{agent} iniciado.", cancellationToken);
        AgentProcessResult result;
        try
        {
            result = isCodex
                ? await codexRunner.RunAsync(prompt, options.Value.AgentWorkingDirectory, cancellationToken)
                : await claudeRunner.RunAsync(prompt, options.Value.AgentWorkingDirectory, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("Falha inesperada no runner {Agent} ({ErrorType}).", agent,
                exception.GetType().Name);
            await botApi.SendMessageAsync(message.Chat.Id, $"{agent} falhou: erro interno de execução.",
                cancellationToken);
            return;
        }

        var response = result.Status switch
        {
            AgentProcessStatus.Succeeded => $"{agent} concluído.\n\n{OutputOrFallback(result.StandardOutput)}",
            AgentProcessStatus.Cancelled => $"{agent} cancelado.",
            _ => $"{agent} falhou.\n\n{FailureDetails(result)}"
        };
        await SendLongMessageAsync(message.Chat.Id, response, cancellationToken);
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
