using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.Agentes;
using Dante.Domain.Agentes;
using Dante.Infrastructure.Contextos;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
// Asks each CLI for its own model list instead of keeping a list in the D.A.N.T.E. (#77): Claude reports it in the
// stream-json initialize response and Codex answers model/list on the app-server. Neither call starts a turn, so no
// model is invoked. The query runs in the General workspace with the General Mode environment, and successful answers
// are cached for a few minutes so that a CLI upgrade is noticed without restarting the Worker.
public sealed class AgentModelCatalog(
    IInteractiveAgentProcessLauncher launcher,
    GeneralWorkspace workspace,
    TimeProvider? time = null) : IAgentModelCatalog
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(5);
    private const int MaxCodexPages = 20;
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim queries = new(1, 1);
    private readonly Dictionary<AgentKind, (DateTimeOffset At, IReadOnlyList<AgentModelInfo> Models)> cache = [];

    // Matches an id or a resolved id, case-insensitively; returns the name as the CLI spells it.
    public static string? Find(IReadOnlyList<AgentModelInfo> models, string name) =>
        models.Select(model => string.Equals(model.Id, name, StringComparison.OrdinalIgnoreCase) ? model.Id
                : string.Equals(model.ResolvedId, name, StringComparison.OrdinalIgnoreCase) ? model.ResolvedId
                : null)
            .FirstOrDefault(found => found is not null);

    public async Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(AgentKind agent,
        CancellationToken cancellationToken = default)
    {
        await queries.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(agent, out var cached) && time.GetUtcNow() - cached.At < CacheDuration)
            {
                return cached.Models;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(QueryTimeout);
            IReadOnlyList<AgentModelInfo> models;
            try
            {
                models = agent == AgentKind.Codex
                    ? await QueryCodexAsync(timeout.Token)
                    : await QueryClaudeAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentModelCatalogException($"o {agent} não informou os modelos a tempo");
            }
            catch (AgentProcessStartException exception)
            {
                throw new AgentModelCatalogException($"o {agent} não pôde ser iniciado", exception);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                                  or AgentProcessInputClosedException)
            {
                throw new AgentModelCatalogException($"o {agent} respondeu fora do protocolo esperado", exception);
            }

            if (models.Count == 0)
            {
                throw new AgentModelCatalogException($"o {agent} não informou nenhum modelo");
            }

            cache[agent] = (time.GetUtcNow(), models);
            return models;
        }
        finally
        {
            queries.Release();
        }
    }

    private async Task<IReadOnlyList<AgentModelInfo>> QueryClaudeAsync(CancellationToken cancellationToken)
    {
        // Same restrictions as a General Mode session; the process only answers initialize and then sees EOF.
        await using var process = await launcher.StartAsync(new AgentProcessRequest(AgentKind.Claude, workspace.Path,
            ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--permission-mode", "plan", "--permission-prompt-tool", "stdio",
                "--restricted", "--strict-mcp-config", "--tools", "Read"],
            IsGeneral: true), cancellationToken: cancellationToken);
        const string requestId = "dante-models";
        await process.WriteLineAsync(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = new JsonObject { ["subtype"] = "initialize" }
        }.ToJsonString(), cancellationToken);
        var response = await ReadUntilAsync(process, message =>
            (string?)message["type"] == "control_response" &&
            (string?)message["response"]?["request_id"] == requestId, cancellationToken);
        await process.StopAsync(CloseGracePeriod);
        if ((string?)response["response"]?["subtype"] != "success")
        {
            throw new AgentModelCatalogException("o Claude recusou o initialize");
        }

        var entries = (response["response"]?["response"]?["models"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(entry => (Id: (string?)entry["value"], Entry: entry))
            .Where(item => AgentModelSelection.IsValidName(item.Id))
            .ToArray();
        // "default" is the CLI default itself, not a model: it marks the model it resolves to.
        var defaultModel = (string?)entries.FirstOrDefault(item => item.Id == "default").Entry?["resolvedModel"];
        var models = new List<AgentModelInfo>();
        foreach (var (id, entry) in entries.Where(item => item.Id != "default"))
        {
            var resolved = (string?)entry["resolvedModel"];
            models.Add(new AgentModelInfo(id!, (string?)entry["displayName"] ?? id!,
                defaultModel is not null && models.All(model => !model.IsDefault) &&
                (resolved == defaultModel || id == defaultModel),
                entry["supportsEffort"]?.GetValue<bool>() == true
                    ? Levels((entry["supportedEffortLevels"] as JsonArray ?? []).Select(level => (string?)level))
                    : [],
                AgentModelSelection.IsValidName(resolved) && resolved != id ? resolved : null));
        }

        return models;
    }

    private async Task<IReadOnlyList<AgentModelInfo>> QueryCodexAsync(CancellationToken cancellationToken)
    {
        await using var process = await launcher.StartAsync(new AgentProcessRequest(AgentKind.Codex, workspace.Path,
            ["app-server", "--listen", "stdio://"], IsGeneral: true), cancellationToken: cancellationToken);
        var nextId = 0;

        async Task<JsonObject> RequestAsync(string method, JsonObject parameters)
        {
            var id = ++nextId;
            await process.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters
            }.ToJsonString(), cancellationToken);
            var reply = await ReadUntilAsync(process, message =>
                message["method"] is null && message["id"] is JsonValue value &&
                value.TryGetValue<int>(out var replyId) && replyId == id, cancellationToken);
            return reply["result"] as JsonObject
                   ?? throw new AgentModelCatalogException($"o Codex recusou {method}");
        }

        await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "dante", ["version"] = "1.0" }
        });
        await process.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" }.ToJsonString(),
            cancellationToken);
        var models = new List<AgentModelInfo>();
        string? cursor = null;
        for (var page = 0; page < MaxCodexPages; page++)
        {
            var result = await RequestAsync("model/list", cursor is null
                ? new JsonObject()
                : new JsonObject { ["cursor"] = cursor });
            foreach (var entry in (result["data"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var id = (string?)entry["model"] ?? (string?)entry["id"];
                if (!AgentModelSelection.IsValidName(id) || entry["hidden"]?.GetValue<bool>() == true) continue;
                models.Add(new AgentModelInfo(id!, (string?)entry["displayName"] ?? id!,
                    entry["isDefault"]?.GetValue<bool>() == true,
                    Levels((entry["supportedReasoningEfforts"] as JsonArray ?? [])
                        .Select(effort => (string?)effort?["reasoningEffort"]))));
            }

            cursor = (string?)result["nextCursor"];
            if (cursor is null) break;
        }

        await process.StopAsync(CloseGracePeriod);
        return models;
    }

    private static IReadOnlyList<string> Levels(IEnumerable<string?> levels) =>
        levels.Where(AgentModelSelection.IsValidEffort).Select(level => level!).ToArray();

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

        throw new AgentModelCatalogException("o processo encerrou antes de informar os modelos");
    }
}
