using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Worker.Agents;

namespace Dante.Worker.Usage;

// Asks the agent's own CLI for the subscription quotas (AD-31), in a short-lived process in the General workspace with
// the General Mode environment, like the model catalog: the account is the one that serves the agents, no session is
// needed or touched, and no turn starts. Codex answers account/rateLimits/read on the app-server; Claude answers the
// experimental get_usage control request on stream-json. Nothing is cached here: every query asks the CLI again.
public sealed class UsageQuotaReader(
    IInteractiveAgentProcessLauncher launcher,
    GeneralWorkspace workspace,
    TimeProvider? time = null,
    TimeSpan? timeout = null) : IUsageQuotaReader
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private const string CodexBucket = "codex";
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim queries = new(1, 1);

    public async Task<UsageReport> ReadAsync(AgentKind agent, CancellationToken cancellationToken = default)
    {
        await queries.WaitAsync(cancellationToken);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                return agent == AgentKind.Codex
                    ? await QueryCodexAsync(limit.Token)
                    : await QueryClaudeAsync(limit.Token);
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

    private async Task<UsageReport> QueryClaudeAsync(CancellationToken cancellationToken)
    {
        // Same restrictions as a General Mode session; the process only answers control requests and then sees EOF.
        await using var process = await launcher.StartAsync(new AgentProcessRequest(AgentKind.Claude, workspace.Path,
            ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--permission-mode", "plan", "--permission-prompt-tool", "stdio",
                "--restricted", "--strict-mcp-config", "--tools", "Read", "--no-session-persistence"],
            IsGeneral: true), cancellationToken: cancellationToken);
        var nextId = 0;

        async Task<(JsonObject? Response, string? Error)> ControlAsync(JsonObject request)
        {
            var id = $"dante-usage-{++nextId}";
            await process.WriteLineAsync(new JsonObject
            {
                ["type"] = "control_request", ["request_id"] = id, ["request"] = request
            }.ToJsonString(), cancellationToken);
            var reply = await ReadUntilAsync(process, message =>
                (string?)message["type"] == "control_response" &&
                (string?)message["response"]?["request_id"] == id, cancellationToken);
            return (string?)reply["response"]?["subtype"] == "success"
                ? (reply["response"]?["response"] as JsonObject ?? [], null)
                : (null, (string?)reply["response"]?["error"] ?? string.Empty);
        }

        // Only how the CLI is authenticated is read: e-mail and organization never leave this method.
        var (initialized, _) = await ControlAsync(new JsonObject { ["subtype"] = "initialize" });
        if (initialized is null)
            throw new UsageQueryException(UsageQueryFailure.Unsupported, "o Claude recusou o initialize");
        var (usage, usageError) = await ControlAsync(new JsonObject
        {
            ["subtype"] = "get_usage", ["skip_behaviors"] = true
        });
        if (usage is null)
        {
            throw usageError!.Contains("Unsupported control request", StringComparison.OrdinalIgnoreCase)
                ? new UsageQueryException(UsageQueryFailure.Unsupported,
                    "a versão instalada do Claude Code não oferece a consulta de cotas; atualize a CLI")
                : new UsageQueryException(UsageQueryFailure.Failed,
                    "o Claude Code não informou as cotas; tente novamente em instantes");
        }

        if (usage["rate_limits_available"]?.GetValue<bool>() != true)
            throw ClaudeWithoutSubscription(initialized["account"] as JsonObject);
        var queriedAt = time.GetUtcNow();
        await process.StopAsync(CloseGracePeriod);
        return ParseClaude(usage, queriedAt);
    }

    // get_usage answers the same way for an API key and for no login; initialize tells them apart.
    private static UsageQueryException ClaudeWithoutSubscription(JsonObject? account) =>
        account?["apiKeySource"] is not null
            ? new UsageQueryException(UsageQueryFailure.NoSubscription,
                "o Claude está autenticado por API key, que não tem cota de assinatura claude.ai")
            : account?["apiProvider"] is JsonValue provider && (string?)provider != "firstParty"
                ? new UsageQueryException(UsageQueryFailure.NoSubscription,
                    "o Claude usa um provedor de nuvem (Bedrock, Vertex…), sem cota de assinatura claude.ai")
                : account?["subscriptionType"] is null
                    ? new UsageQueryException(UsageQueryFailure.NotAuthenticated,
                        "o Claude não está autenticado com uma conta claude.ai neste host; entre com `claude` e /login na " +
                        "máquina do D.A.N.T.E.")
                    : new UsageQueryException(UsageQueryFailure.NoSubscription,
                        "a conta do Claude não informou cota de assinatura");

    // Documented fields of get_usage only: five_hour is the session window and seven_day the general week; per-model
    // weeks (seven_day_opus, seven_day_sonnet, model_scoped) and OAuth apps are shown apart, never instead of the week.
    // utilization is already 0–100 here (rate_limit_event uses 0–1). Keys outside the schema are ignored.
    internal static UsageReport ParseClaude(JsonObject usage, DateTimeOffset queriedAt)
    {
        if (usage["rate_limits"] is not JsonObject limits)
        {
            throw new UsageQueryException(UsageQueryFailure.Failed,
                "o serviço do Claude não informou as cotas; tente novamente em instantes");
        }

        QuotaMetric Metric(string key, TimeSpan duration, string missing) =>
            ClaudeWindow(limits[key], duration) is { } window ? QuotaMetric.Of(window) : QuotaMetric.Unavailable(missing);

        var additional = new List<QuotaWindow>();
        void Add(JsonNode? node, string label)
        {
            if (ClaudeWindow(node, Week) is { } window &&
                additional.All(other => !string.Equals(other.Label, label, StringComparison.OrdinalIgnoreCase)))
                additional.Add(window with { Label = label });
        }

        Add(limits["seven_day_opus"], "Opus");
        Add(limits["seven_day_sonnet"], "Sonnet");
        foreach (var scoped in (limits["model_scoped"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)scoped["display_name"] is { Length: > 0 } name) Add(scoped, name);
        }
        Add(limits["seven_day_oauth_apps"], "Apps OAuth");

        return new UsageReport(AgentKind.Claude, queriedAt,
            Metric("five_hour", TimeSpan.FromHours(5), "o Claude não informou a janela de sessão"),
            Metric("seven_day", Week, "o Claude não informou a janela semanal"),
            additional,
            "A CLI do Claude pode responder com uma leitura própria de até 1 min (até 1 h se o serviço falhar).");
    }

    private static QuotaWindow? ClaudeWindow(JsonNode? node, TimeSpan duration)
    {
        if (node is not JsonObject window || window["utilization"] is not JsonValue used) return null;
        var percent = used.GetValue<decimal>();
        if (percent < 0) return null;
        return new QuotaWindow(percent,
            (string?)window["resets_at"] is { } resetsAt
                ? DateTimeOffset.Parse(resetsAt, System.Globalization.CultureInfo.InvariantCulture)
                : null,
            duration);
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

    // Buckets are keyed by limitId; "codex" is the general quota and the others are shown apart, never added to it.
    // Windows are identified by their duration, not by primary/secondary: the week lasts 7 days and the session window
    // is the only one shorter than a day. An ambiguous or missing window is unavailable, not guessed.
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
            buckets.Add(((string?)single["limitId"] ?? CodexBucket, (string?)single["limitName"], single));
        }

        var main = buckets.FindIndex(bucket => bucket.Id == CodexBucket) is var index and >= 0 ? index
            : buckets.Count == 1 ? 0 : -1;
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
            var shorts = windows.Where(window => window.Duration < TimeSpan.FromDays(1)).ToArray();
            weekly = weeks.Length == 1 ? QuotaMetric.Of(weeks[0]) : QuotaMetric.Unavailable(weeks.Length == 0
                ? "o Codex não informou uma janela semanal"
                : "o Codex informou mais de uma janela semanal");
            session = shorts.Length == 1 ? QuotaMetric.Of(shorts[0]) : QuotaMetric.Unavailable(shorts.Length == 0
                ? "o Codex não informou uma janela de sessão"
                : "o Codex informou mais de uma janela curta");
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
        var percent = used.GetValue<decimal>();
        if (percent < 0) return null;
        var minutes = window["windowDurationMins"]?.GetValue<long>();
        var resetsAt = window["resetsAt"]?.GetValue<long>();
        return new QuotaWindow(percent,
            resetsAt is null ? null : DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value),
            minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null);
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
