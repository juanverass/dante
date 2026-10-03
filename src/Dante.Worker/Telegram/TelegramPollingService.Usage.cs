using Dante.Worker.Agents;
using Dante.Worker.Usage;

namespace Dante.Worker.Telegram;

// /uso claude|codex (#116, #117): the subscription quotas of the agent's account (AD-31). It never reaches an agent's prompt
// and never creates, changes or interrupts a session, a job or a pending request.
public sealed partial class TelegramPollingService
{
    private const string UsageSyntax = "Uso: /uso claude|codex";

    private async Task<string> HandleUsageCommandAsync(string prompt, CancellationToken cancellationToken)
    {
        var parts = prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        AgentKind? agent = parts.Length != 1 ? null : parts[0].ToLowerInvariant() switch
        {
            "claude" => AgentKind.Claude,
            "codex" => AgentKind.Codex,
            _ => null
        };
        if (agent is null) return UsageSyntax;
        if (usage is null) return "Consulta de cotas indisponível.";
        try
        {
            var report = await usage.ReadAsync(agent.Value, cancellationToken);
            return UsageReportFormatter.Format(report, TimeProvider.System.GetUtcNow());
        }
        catch (UsageQueryException exception)
        {
            logger.LogInformation("Consulta de cotas do {Agent} falhou ({Failure}).", agent, exception.Failure);
            return $"Uso — {agent}\nNão foi possível consultar as cotas: {exception.Message.TrimEnd('.')}.";
        }
    }
}
