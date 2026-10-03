using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Worker.Agents;

namespace Dante.Worker.Usage;

// Asks the agent's own CLI for the subscription quotas (AD-31), in a short-lived process in the General workspace with
// the General Mode environment, like the model catalog: the account is the one that serves the agents, no session is
// needed or touched, and no turn starts. Codex answers account/rateLimits/read on the app-server. Nothing is cached:
// every query reads the provider again.
public sealed class UsageQuotaReader(
    IInteractiveAgentProcessLauncher launcher,
    GeneralWorkspace workspace,
    TimeProvider? time = null,
    TimeSpan? timeout = null) : IUsageQuotaReader
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private static readonly TimeSpan CodexSession = TimeSpan.FromHours(5);
    private const string CodexBucket = "codex";
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim queries = new(1, 1);

    public async Task<UsageReport> ReadAsync(AgentKind agent, CancellationToken cancellationToken = default)
    {
        if (agent != AgentKind.Codex)
        {
            throw new UsageQueryException(UsageQueryFailure.Unsupported,
                "a consulta de cotas do Claude ainda não está disponível no D.A.N.T.E.");
        }

        await queries.WaitAsync(cancellationToken);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                return await QueryCodexAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UsageQueryException(UsageQueryFailure.Timeout,
                    $"o {agent} não informou as cotas a tempo; tente novamente");
            }
            catch (AgentProcessStartException exception)
            {
                throw new UsageQueryException(UsageQueryFailure.Failed,
                    $"o {agent} não pôde ser iniciado neste host", exception);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                                  or FormatException or AgentProcessInputClosedException)
            {
                throw new UsageQueryException(UsageQueryFailure.Unsupported,
                    $"o {agent} respondeu fora do protocolo esperado; confira a versão da CLI", exception);
            }
        }
        finally
        {
            queries.Release();
        }
    }

    private async Task<UsageReport> QueryCodexAsync(CancellationToken cancellationToken)
    {
        await using var process = await launcher.StartAsync(new AgentProcessRequest(AgentKind.Codex, workspace.Path,
            ["app-server", "--listen", "stdio://"], IsGeneral: true), cancellationToken: cancellationToken);
        var nextId = 0;

        async Task<(JsonObject? Result, string? Error)> RequestAsync(string method, JsonObject parameters)
        {
            var id = ++nextId;
            await process.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters
            }.ToJsonString(), cancellationToken);
            var reply = await ReadUntilAsync(process, message =>
                message["method"] is null && message["id"] is JsonValue value &&
                value.TryGetValue<int>(out var replyId) && replyId == id, cancellationToken);
            return reply["result"] is JsonObject result
                ? (result, null)
                : (null, (string?)reply["error"]?["message"] ?? string.Empty);
        }

        var (initialized, _) = await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "dante", ["version"] = "1.0" }
        });
        if (initialized is null)
            throw new UsageQueryException(UsageQueryFailure.Unsupported, "o Codex recusou o initialize");
        await process.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" }.ToJsonString(),
            cancellationToken);

        // Only the account type is read: e-mail and ids never leave this method.
        var (account, accountError) = await RequestAsync("account/read", new JsonObject());
        if (account is null) throw CodexFailure(accountError!);
        switch ((string?)account["account"]?["type"])
        {
            case null:
                throw new UsageQueryException(UsageQueryFailure.NotAuthenticated,
                    "o Codex não está autenticado neste host; entre com `codex login` na máquina do D.A.N.T.E.");
            case "chatgpt":
                break;
            case "apiKey":
                throw new UsageQueryException(UsageQueryFailure.NoSubscription,
                    "o Codex está autenticado por API key, que não tem cota de assinatura ChatGPT");
            default:
                throw new UsageQueryException(UsageQueryFailure.NoSubscription,
                    "o Codex não está autenticado com uma conta ChatGPT, então não há cota de assinatura");
        }

        var (limits, limitsError) = await RequestAsync("account/rateLimits/read",
            new JsonObject { ["excludeResetCreditDetails"] = true });
        if (limits is null) throw CodexFailure(limitsError!);
        var queriedAt = time.GetUtcNow();
        await process.StopAsync(CloseGracePeriod);
        return ParseCodex(limits, queriedAt);
    }

    private static UsageQueryException CodexFailure(string error) =>
        error.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            ? new UsageQueryException(UsageQueryFailure.NotAuthenticated,
                "o Codex não está autenticado neste host; entre com `codex login` na máquina do D.A.N.T.E.")
            : error.Contains("unknown variant", StringComparison.OrdinalIgnoreCase)
                ? new UsageQueryException(UsageQueryFailure.Unsupported,
                    "a versão instalada do Codex não oferece a consulta de cotas; atualize a CLI")
                : new UsageQueryException(UsageQueryFailure.Failed,
                    "o serviço do Codex não informou as cotas; tente novamente em instantes");

    // Buckets are keyed by limitId; only "codex" is the general quota. Any other bucket, even alone, is shown apart and
    // never replaces or adds to it. Windows are identified by the durations proven in #115, not by primary/secondary:
    // 300 min is the session and 10080 min the week. Any other window is shown apart; an ambiguous or missing window is
    // unavailable, not guessed.
    internal static UsageReport ParseCodex(JsonObject result, DateTimeOffset queriedAt)
    {
        var buckets = new List<(string Id, string? Name, JsonObject Snapshot)>();
        if (result["rateLimitsByLimitId"] is JsonObject byLimitId && byLimitId.Count > 0)
        {
            foreach (var (id, snapshot) in byLimitId)
            {
                if (snapshot is JsonObject bucket) buckets.Add((id, (string?)bucket["limitName"], bucket));
            }
        }
        else if (result["rateLimits"] is JsonObject single)
        {
            buckets.Add(((string?)single["limitId"] ?? "limite sem identificação", (string?)single["limitName"], single));
        }

        var main = buckets.FindIndex(bucket => bucket.Id == CodexBucket);
        var additional = new List<QuotaWindow>();
        QuotaMetric session, weekly;
        if (main < 0)
        {
            session = weekly = QuotaMetric.Unavailable("o Codex não informou a cota geral da conta");
        }
        else
        {
            var windows = Windows(buckets[main].Snapshot).ToArray();
            var weeks = windows.Where(window => window.Duration == Week).ToArray();
            var sessions = windows.Where(window => window.Duration == CodexSession).ToArray();
            weekly = weeks.Length == 1 ? QuotaMetric.Of(weeks[0]) : QuotaMetric.Unavailable(weeks.Length == 0
                ? "o Codex não informou uma janela semanal"
                : "o Codex informou mais de uma janela semanal");
            session = sessions.Length == 1 ? QuotaMetric.Of(sessions[0]) : QuotaMetric.Unavailable(sessions.Length == 0
                ? "o Codex não informou uma janela de sessão de 5h"
                : "o Codex informou mais de uma janela de 5h");
            additional.AddRange(windows
                .Where(window => !ReferenceEquals(window, weekly.Window) && !ReferenceEquals(window, session.Window))
                .Select(window => window with { Label = "Codex" }));
        }

        for (var position = 0; position < buckets.Count; position++)
        {
            if (position == main) continue;
            var label = string.IsNullOrWhiteSpace(buckets[position].Name) ? buckets[position].Id : buckets[position].Name!;
            additional.AddRange(Windows(buckets[position].Snapshot).Select(window => window with { Label = label }));
        }

        return new UsageReport(AgentKind.Codex, queriedAt, session, weekly, additional);
    }

    private static IEnumerable<QuotaWindow> Windows(JsonObject bucket) =>
        new[] { bucket["primary"], bucket["secondary"] }.Select(Window).OfType<QuotaWindow>();

    private static QuotaWindow? Window(JsonNode? node)
    {
        if (node is not JsonObject window || window["usedPercent"] is not JsonValue used) return null;
        var percent = Percent(used);
        var minutes = window["windowDurationMins"]?.GetValue<long>();
        var resetsAt = window["resetsAt"]?.GetValue<long>();
        return new QuotaWindow(percent,
            resetsAt is null ? null : DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value),
            minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null);
    }

    // A share of the limit outside 0–100 means the answer is not the documented one: it is refused (and read as an
    // unsupported CLI), never clamped.
    private static decimal Percent(JsonValue value)
    {
        var percent = value.GetValue<decimal>();
        return percent is >= 0 and <= 100 ? percent
            : throw new FormatException($"percentual de cota fora de 0–100: {percent}");
    }

    // stdout carries the protocol; stderr and unrelated messages (notifications, other responses) are skipped.
    private static async Task<JsonObject> ReadUntilAsync(InteractiveAgentProcess process, Func<JsonObject, bool> match,
        CancellationToken cancellationToken)
    {
        while (await process.Output.WaitToReadAsync(cancellationToken))
        {
            while (process.Output.TryRead(out var line))
            {
                if (line.Stream != AgentOutputStream.StandardOutput || string.IsNullOrWhiteSpace(line.Text)) continue;
                if (JsonNode.Parse(line.Text) is JsonObject message && match(message)) return message;
            }
        }

        throw new UsageQueryException(UsageQueryFailure.Failed, "a CLI encerrou antes de informar as cotas");
    }
}
