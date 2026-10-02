using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Attachments;

namespace Dante.Worker.Sessions;

// Codex over `codex app-server --listen stdio://` (AD-15, AD-19): JSON-RPC in JSONL on one process per session, with
// one ephemeral thread per D.A.N.T.E. session. Approvals and user input are server requests answered by id;
// steer and interrupt are client requests on the active turn.
public sealed class CodexSessionDriver(IInteractiveAgentProcessLauncher launcher) : IAgentSessionDriver
{
    private const int EventCapacity = 256;
    private const string UserInputMethod = "item/tool/requestUserInput";
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(10);

    // Bounded like the process output (AD-17): a slow consumer pauses the reader and, through the pipe, the agent.
    private readonly Channel<AgentEvent> events = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(EventCapacity) { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> awaitingResponses = [];
    private readonly Dictionary<string, PendingServerRequest> pendingRequests = [];
    private InteractiveAgentProcess? process;
    private string? threadId;
    private string? model;
    private string? effort;
    private string? activeTurnId;
    private string? completedTurnId;
    private AgentPermissionProfile profile;
    private bool closing;
    private bool ended;
    private long nextRequestId;
    private int started;

    public AgentDriverCapabilities Capabilities => AgentDriverCapabilities.Codex;

    public async Task<AgentSessionStarted> StartAsync(
        AgentSessionStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.Exchange(ref started, 1) == 1)
        {
            throw new InvalidOperationException("Este driver já iniciou uma sessão.");
        }

        var (approvalPolicy, sandbox) = Policy(options.Profile);
        // The prompt never goes on the command line: turns are JSON-RPC requests on stdin.
        var request = new AgentProcessRequest(
            AgentKind.Codex,
            options.WorkingDirectory,
            ["app-server", "--listen", "stdio://"],
            options.IsGeneral,
            options.EnvironmentVariables);
        var agent = await launcher.StartAsync(request, cancellationToken: cancellationToken);
        lock (gate)
        {
            process = agent;
            profile = options.Profile;
            effort = options.ModelSelection?.Effort;
        }

        _ = ReadOutputAsync(agent);
        try
        {
            // experimentalApi enables item/tool/requestUserInput (plan collaboration mode on 0.157.1).
            await SendRequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "dante", ["version"] = "1.0" },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true }
            }, cancellationToken);
            await WriteAsync(agent, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" },
                cancellationToken);
            var threadParameters = new JsonObject
            {
                ["cwd"] = options.WorkingDirectory,
                ["approvalPolicy"] = approvalPolicy,
                ["sandbox"] = sandbox,
                // D.A.N.T.E. sessions live only while the Worker runs (Epic #60).
                ["ephemeral"] = true
            };
            // Without a selection the CLI picks its own default model (#77); thread/start reports it either way.
            if (options.ModelSelection?.Model is { } selectedModel) threadParameters["model"] = selectedModel;
            var thread = await SendRequestAsync("thread/start", threadParameters, cancellationToken);
            var id = GetString(thread["thread"] as JsonObject, "id")
                     ?? throw new AgentProtocolException("O Codex iniciou a thread sem id.");
            lock (gate)
            {
                threadId = id;
                model = GetString(thread, "model");
            }

            return new AgentSessionStarted(id, agent.ProcessId, GetString(thread, "model"));
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    // Only with the session idle: a turn/start during an active turn is absorbed by it instead of queued (AD-16).
    public async Task StartTurnAsync(AgentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
        string thread;
        AgentPermissionProfile current;
        string? currentModel;
        lock (gate)
        {
            thread = RequireThread();
            current = profile;
            currentModel = model;
        }

        var parameters = new JsonObject { ["threadId"] = thread, ["input"] = Input(input) };
        if (effort is not null) parameters["effort"] = effort;
        if (current == AgentPermissionProfile.Plan)
        {
            // The plan collaboration mode needs the model explicitly; thread/start reported it.
            parameters["collaborationMode"] = new JsonObject
            {
                ["mode"] = "plan",
                ["settings"] = new JsonObject { ["model"] = currentModel, ["developer_instructions"] = null }
            };
        }

        if (effort is not null && parameters["collaborationMode"]?["settings"] is JsonObject collaborationSettings)
            collaborationSettings["reasoning_effort"] = effort;

        var result = await SendRequestAsync("turn/start", parameters, cancellationToken);
        var turnId = GetString(result["turn"] as JsonObject, "id")
                     ?? throw new AgentProtocolException("O Codex iniciou o turno sem id.");
        lock (gate)
        {
            // turn/started may already have set it; the response is authoritative, unless a fast turn already
            // completed while the response was on its way: restoring it would leave a stale active turn behind.
            if (turnId != completedTurnId) activeTurnId = turnId;
        }
    }

    // Applied at the next model boundary of the active turn, not preemptively.
    public async Task SteerAsync(AgentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
        string thread;
        string turn;
        lock (gate)
        {
            thread = RequireThread();
            turn = activeTurnId ?? throw new InvalidOperationException("Não há turno ativo no Codex para orientar.");
        }

        await SendRequestAsync("turn/steer", new JsonObject
        {
            ["threadId"] = thread,
            ["expectedTurnId"] = turn,
            ["input"] = Input(input)
        }, cancellationToken);
    }

    // Pending approvals are cancelled and pending questions answered empty first, so nothing upstream keeps waiting;
    // the turn then ends with turn/completed status interrupted and the process keeps serving the thread.
    public async Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        string thread;
        string? turn;
        PendingServerRequest[] pending;
        lock (gate)
        {
            thread = RequireThread();
            turn = activeTurnId;
            pending = [.. pendingRequests.Values];
            pendingRequests.Clear();
        }

        var agent = RequireOpen();
        foreach (var request in pending)
        {
            await WriteAsync(agent, Response(request.Id, request.Method == UserInputMethod
                ? new JsonObject { ["answers"] = new JsonObject() }
                : new JsonObject { ["decision"] = "cancel" }), cancellationToken);
        }

        if (turn is null)
        {
            // The turn already completed.
            return;
        }

        try
        {
            await SendRequestAsync("turn/interrupt", new JsonObject { ["threadId"] = thread, ["turnId"] = turn },
                cancellationToken);
        }
        catch (InvalidOperationException) when (TurnEnded(turn))
        {
            // The turn completed while the interrupt was on its way.
        }
    }

    private bool TurnEnded(string turnId)
    {
        lock (gate)
        {
            return activeTurnId != turnId;
        }
    }

    public Task RespondAsync(
        string upstreamRequestId,
        AgentUserResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamRequestId);
        ArgumentNullException.ThrowIfNull(response);
        var agent = RequireOpen();
        JsonObject result;
        JsonNode id;
        lock (gate)
        {
            if (!pendingRequests.TryGetValue(upstreamRequestId, out var pending))
            {
                throw new InvalidOperationException($"A solicitação {upstreamRequestId} não está pendente no Codex.");
            }

            result = (pending.Method == UserInputMethod, response) switch
            {
                (false, AgentApprovalResponse approval) => new JsonObject { ["decision"] = Decision(approval) },
                (true, AgentInputResponse answers) => InputResult(pending, answers),
                _ => throw new ArgumentException(
                    $"A solicitação {upstreamRequestId} não aceita esse tipo de resposta.", nameof(response))
            };
            pendingRequests.Remove(upstreamRequestId);
            id = pending.Id;
        }

        return WriteAsync(agent, Response(id, result), cancellationToken);
    }

    // Completes normally after CloseAsync; ends with AgentProtocolException when the protocol breaks or the process
    // dies on its own.
    public IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
        events.Reader.ReadAllAsync(cancellationToken);

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        InteractiveAgentProcess? agent;
        lock (gate)
        {
            closing = true;
            agent = process;
        }

        if (agent is not null)
        {
            // The app-server ends when stdin reaches EOF; StopAsync kills the tree after the grace period.
            await agent.StopAsync(CloseGracePeriod).WaitAsync(cancellationToken);
        }
        else
        {
            End(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        InteractiveAgentProcess? agent;
        lock (gate)
        {
            closing = true;
            agent = process;
        }

        if (agent is not null)
        {
            await agent.DisposeAsync();
        }

        await lifetime.CancelAsync();
        End(null);
    }

    // approvalPolicy and sandbox of thread/start. No profile reaches danger-full-access; that choice is #67's.
    private static (string ApprovalPolicy, string Sandbox) Policy(AgentPermissionProfile profile) => profile switch
    {
        AgentPermissionProfile.Manual => ("on-request", "workspace-write"),
        AgentPermissionProfile.Auto => ("never", "workspace-write"),
        AgentPermissionProfile.Plan => ("on-request", "read-only"),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Perfil de permissão desconhecido.")
    };

    // Codex decisions carry no message: a denial reason stays with the D.A.N.T.E.
    private static string Decision(AgentApprovalResponse approval) => approval.Decision switch
    {
        AgentApprovalDecision.ApproveOnce => "accept",
        AgentApprovalDecision.ApproveForSession => "acceptForSession",
        _ => "decline"
    };

    private static JsonObject InputResult(PendingServerRequest pending, AgentInputResponse response)
    {
        var answers = new JsonObject();
        foreach (var (id, answer) in response.Answers)
        {
            if (!pending.QuestionIds.Contains(id))
            {
                throw new ArgumentException($"A pergunta {id} não existe nesta solicitação.", nameof(response));
            }

            answers[id] = new JsonObject { ["answers"] = new JsonArray(answer) };
        }

        return new JsonObject { ["answers"] = answers };
    }

    // The text, then one localImage per image in the order sent; the CLI reads each file by absolute path, so it may
    // stay outside the cwd and the sandbox (#93 spike, AD-29). Used by turn/start and turn/steer alike.
    private static JsonArray Input(AgentInput input)
    {
        var items = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = input.Text });
        foreach (var attachment in input.Attachments)
        {
            if (attachment.Kind != AttachmentKind.Image)
                throw new ArgumentException("O Codex só recebe imagens como anexo.", nameof(input));
            items.Add(new JsonObject { ["type"] = "localImage", ["path"] = attachment.Path });
        }

        return items;
    }

    private static JsonObject Response(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private async Task ReadOutputAsync(InteractiveAgentProcess agent)
    {
        try
        {
            await foreach (var line in agent.Output.ReadAllAsync(lifetime.Token))
            {
                // stderr carries logs, not protocol messages.
                if (line.Stream == AgentOutputStream.StandardError || string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                await HandleAsync(Parse(line.Text));
            }

            var exit = await agent.Completion;
            bool expected;
            lock (gate)
            {
                expected = closing;
            }

            End(expected ? null : new AgentProtocolException(
                $"O processo do Codex encerrou inesperadamente (código {exit.ExitCode})."));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            End(null);
        }
        catch (Exception exception)
        {
            End(exception as AgentProtocolException
                ?? new AgentProtocolException("Falha ao ler a saída do Codex.", exception));
            agent.Kill();
        }
    }

    private static JsonObject Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject
                   ?? throw new AgentProtocolException("O Codex enviou uma mensagem JSON-RPC que não é um objeto.");
        }
        catch (JsonException exception)
        {
            throw new AgentProtocolException("O Codex enviou uma linha fora do protocolo JSON-RPC.", exception);
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        var method = GetString(message, "method");
        var id = message["id"];
        if (method is null)
        {
            if (id is not null)
            {
                HandleResponse(id, message);
            }

            return;
        }

        var parameters = message["params"] as JsonObject ?? [];
        if (id is not null)
        {
            await HandleServerRequestAsync(id, method, parameters);
        }
        else
        {
            await HandleNotificationAsync(method, parameters);
        }
    }

    private void HandleResponse(JsonNode id, JsonObject message)
    {
        TaskCompletionSource<JsonObject>? waiter;
        lock (gate)
        {
            if (id.GetValueKind() != JsonValueKind.Number || !awaitingResponses.Remove(id.GetValue<long>(), out waiter))
            {
                return;
            }
        }

        if (message["error"] is JsonObject error)
        {
            // A rejected request (e.g. steer after the turn ended) fails that call, not the session.
            waiter.TrySetException(new InvalidOperationException(
                $"O Codex recusou a solicitação: {GetString(error, "message") ?? "erro desconhecido"}."));
        }
        else
        {
            waiter.TrySetResult(message["result"] as JsonObject ?? []);
        }
    }

    private async Task HandleServerRequestAsync(JsonNode id, string method, JsonObject parameters)
    {
        var upstreamId = IdKey(id);
        AgentEvent requested;
        PendingServerRequest pending;
        switch (method)
        {
            case "item/commandExecution/requestApproval":
                pending = new PendingServerRequest(id.DeepClone(), method, []);
                requested = new ApprovalRequestedEvent(upstreamId, AgentToolKind.Command,
                    GetString(parameters, "command") ?? "comando", GetString(parameters, "reason"))
                { CanApproveForSession = true };
                break;
            case "item/fileChange/requestApproval":
                pending = new PendingServerRequest(id.DeepClone(), method, []);
                requested = new ApprovalRequestedEvent(upstreamId, AgentToolKind.FileChange,
                    GetString(parameters, "grantRoot") is { } root ? $"escrever em {root}" : "alterar arquivos",
                    GetString(parameters, "reason"))
                { CanApproveForSession = true };
                break;
            case UserInputMethod:
                var questions = (parameters["questions"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(question => new AgentQuestion(
                        GetString(question, "id") ?? "",
                        GetString(question, "question") ?? "",
                        (question["options"] as JsonArray ?? [])
                        .OfType<JsonObject>()
                        .Select(option => GetString(option, "label") ?? "")
                        .ToArray()))
                    .ToArray();
                pending = new PendingServerRequest(id.DeepClone(), method, [.. questions.Select(q => q.Id)]);
                requested = new UserInputRequestedEvent(upstreamId, questions);
                break;
            default:
                // Elicitation, permission profiles, dynamic tools, auth refresh…: not enabled by the D.A.N.T.E.;
                // an error keeps Codex from waiting forever.
                await WriteAsync(RequireOpen(), new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32601,
                        ["message"] = $"{method} não é suportado pelo D.A.N.T.E."
                    }
                }, lifetime.Token);
                return;
        }

        // Registered before the event leaves, so an immediate answer finds it.
        lock (gate)
        {
            pendingRequests[upstreamId] = pending;
        }

        await EmitAsync(requested);
    }

    private async Task HandleNotificationAsync(string method, JsonObject parameters)
    {
        switch (method)
        {
            case "turn/started":
                lock (gate)
                {
                    activeTurnId = GetString(parameters["turn"] as JsonObject, "id") ?? activeTurnId;
                }

                await EmitAsync(new TurnStartedEvent());
                break;
            case "item/agentMessage/delta" when GetString(parameters, "delta") is { Length: > 0 } delta:
                await EmitAsync(new MessageDeltaEvent(GetString(parameters, "itemId") ?? "", delta));
                break;
            case "item/started" when parameters["item"] is JsonObject item:
                if (ToolStarted(item) is { } toolStarted)
                {
                    await EmitAsync(toolStarted);
                }

                break;
            case "item/completed" when parameters["item"] is JsonObject item:
                await HandleItemCompletedAsync(item);
                break;
            case "turn/completed":
                await HandleTurnCompletedAsync(parameters["turn"] as JsonObject);
                break;
            case "serverRequest/resolved" when parameters["requestId"] is { } requestId:
                // Codex resolved it by itself (e.g. the turn ended); a late answer is refused.
                lock (gate)
                {
                    pendingRequests.Remove(IdKey(requestId));
                }

                break;
            case "error" when parameters["error"] is JsonObject error:
                var text = GetString(error, "message") ?? "erro desconhecido";
                await EmitAsync(parameters["willRetry"]?.GetValueKind() == JsonValueKind.True
                    ? new WarningEvent(text)
                    : new ErrorEvent(text));
                break;
            case "warning" when GetString(parameters, "message") is { } warning:
                await EmitAsync(new WarningEvent(warning));
                break;
            // Deltas of reasoning and command output, diffs, thread status, token usage…: not session events.
        }
    }

    private static ToolStartedEvent? ToolStarted(JsonObject item)
    {
        var itemId = GetString(item, "id") ?? "";
        return GetString(item, "type") switch
        {
            "commandExecution" => new ToolStartedEvent(itemId, AgentToolKind.Command, GetString(item, "command") ?? ""),
            "fileChange" => new ToolStartedEvent(itemId, AgentToolKind.FileChange, string.Join(", ", ChangedPaths(item))),
            "mcpToolCall" => new ToolStartedEvent(itemId, AgentToolKind.Tool,
                $"{GetString(item, "server")}.{GetString(item, "tool")}"),
            "dynamicToolCall" => new ToolStartedEvent(itemId, AgentToolKind.Tool, GetString(item, "tool") ?? ""),
            "webSearch" => new ToolStartedEvent(itemId, AgentToolKind.Tool, $"webSearch {GetString(item, "query")}"),
            _ => null
        };
    }

    private async Task HandleItemCompletedAsync(JsonObject item)
    {
        var itemId = GetString(item, "id") ?? "";
        var status = GetString(item, "status");
        switch (GetString(item, "type"))
        {
            case "agentMessage":
                await EmitAsync(new MessageCompletedEvent(itemId, GetString(item, "text") ?? ""));
                break;
            case "commandExecution":
                var exitCode = item["exitCode"] is JsonValue code && code.GetValueKind() == JsonValueKind.Number
                    ? code.GetValue<int>()
                    : 0;
                await EmitAsync(new ToolCompletedEvent(itemId, AgentToolKind.Command,
                    status == "completed" && exitCode == 0, GetString(item, "aggregatedOutput")));
                break;
            case "fileChange":
                var applied = status == "completed";
                await EmitAsync(new ToolCompletedEvent(itemId, AgentToolKind.FileChange, applied));
                if (applied)
                {
                    var diffs = (item["changes"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(change => GetString(change, "diff")).Where(diff => !string.IsNullOrEmpty(diff));
                    await EmitAsync(new FileChangeEvent(ChangedPaths(item), string.Join("\n", diffs) is { Length: > 0 } diff
                        ? diff
                        : null));
                }

                break;
            case "mcpToolCall" or "dynamicToolCall":
                await EmitAsync(new ToolCompletedEvent(itemId, AgentToolKind.Tool, status == "completed"));
                break;
            case "webSearch":
                await EmitAsync(new ToolCompletedEvent(itemId, AgentToolKind.Tool, true));
                break;
        }
    }

    private async Task HandleTurnCompletedAsync(JsonObject? turn)
    {
        lock (gate)
        {
            completedTurnId = GetString(turn, "id");
            activeTurnId = null;
            pendingRequests.Clear();
        }

        await EmitAsync(GetString(turn, "status") switch
        {
            "completed" => new TurnCompletedEvent(AgentTurnOutcome.Completed),
            "interrupted" => new TurnCompletedEvent(AgentTurnOutcome.Interrupted),
            var status => new TurnCompletedEvent(AgentTurnOutcome.Failed,
                GetString(turn?["error"] as JsonObject, "message") ?? status)
        });
    }

    private async Task<JsonObject> SendRequestAsync(string method, JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var agent = RequireOpen();
        var id = Interlocked.Increment(ref nextRequestId);
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (ended)
            {
                throw new AgentProtocolException("A sessão do Codex já foi encerrada.");
            }

            awaitingResponses[id] = waiter;
        }

        await WriteAsync(agent, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        }, cancellationToken);
        return await waiter.Task.WaitAsync(cancellationToken);
    }

    private static Task WriteAsync(InteractiveAgentProcess agent, JsonObject message,
        CancellationToken cancellationToken) =>
        agent.WriteLineAsync(message.ToJsonString(), cancellationToken);

    private ValueTask EmitAsync(AgentEvent agentEvent) => events.Writer.WriteAsync(agentEvent, lifetime.Token);

    private InteractiveAgentProcess RequireOpen()
    {
        lock (gate)
        {
            if (process is null)
            {
                throw new InvalidOperationException("A sessão do Codex ainda não foi iniciada.");
            }

            if (closing || ended)
            {
                throw new InvalidOperationException("A sessão do Codex está encerrada.");
            }

            return process;
        }
    }

    // Callers hold gate.
    private string RequireThread()
    {
        if (closing || ended)
        {
            throw new InvalidOperationException("A sessão do Codex está encerrada.");
        }

        return threadId ?? throw new InvalidOperationException("A sessão do Codex ainda não foi iniciada.");
    }

    // Ends the event stream once; requests still waiting for Codex fail with the same error.
    private void End(Exception? error)
    {
        TaskCompletionSource<JsonObject>[] waiters;
        lock (gate)
        {
            if (ended)
            {
                return;
            }

            ended = true;
            waiters = [.. awaitingResponses.Values];
            awaitingResponses.Clear();
            pendingRequests.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetException(error ?? new AgentProtocolException("A sessão do Codex foi encerrada."));
        }

        events.Writer.TryComplete(error);
    }

    private static string[] ChangedPaths(JsonObject item) =>
        (item["changes"] as JsonArray ?? []).OfType<JsonObject>()
        .Select(change => GetString(change, "path"))
        .OfType<string>()
        .ToArray();

    // JSON-RPC ids may be numbers or strings; the text form is the upstream request id.
    private static string IdKey(JsonNode id) =>
        id.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : id.ToJsonString();

    private static string? GetString(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private sealed record PendingServerRequest(JsonNode Id, string Method, IReadOnlyCollection<string> QuestionIds);
}
