using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Attachments;

namespace Dante.Worker.Sessions;

// Claude Code over bidirectional stream-json (AD-15, AD-18): one `claude --print` process per session receives every
// turn on stdin. Approvals and AskUserQuestion arrive as can_use_tool control requests; interrupt is a control
// request too. The upstream session id is fixed with --session-id, so it is known before the first turn.
public sealed class ClaudeSessionDriver(
    IInteractiveAgentProcessLauncher launcher,
    TimeSpan? contextTimeout = null,
    TimeSpan? compactTimeout = null) : IAgentSessionDriver
{
    private const int EventCapacity = 256;
    private const string AskUserQuestionTool = "AskUserQuestion";
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(10);
    // /clear is local to the CLI (it answered in milliseconds in #119).
    private readonly TimeSpan clearTimeout = contextTimeout ?? TimeSpan.FromSeconds(30);
    // Compaction summarizes the conversation with the model: it grows with the context (6–12 s on short ones in #119).
    private readonly TimeSpan compactTimeout = compactTimeout ?? TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InterruptedOperationGrace = TimeSpan.FromSeconds(30);

    // Bounded like the process output (AD-17): a slow consumer pauses the reader and, through the pipe, the agent.
    private readonly Channel<AgentEvent> events = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(EventCapacity) { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> awaitingResponses = [];
    private readonly Dictionary<string, PendingControl> pendingControls = [];
    private readonly Dictionary<string, ToolUse> tools = [];
    private InteractiveAgentProcess? process;
    // A /clear or /compact in flight (#120, #121): its messages are the driver's, not a turn of the conversation.
    private ContextOperation? operation;
    private string? currentMessageId;
    private bool interruptRequested;
    private bool closing;
    private bool ended;
    private long nextControlId;
    private int started;

    public AgentDriverCapabilities Capabilities => AgentDriverCapabilities.Claude;

    public async Task<AgentSessionStarted> StartAsync(
        AgentSessionStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.Exchange(ref started, 1) == 1)
        {
            throw new InvalidOperationException("Este driver já iniciou uma sessão.");
        }

        var sessionId = Guid.NewGuid().ToString();
        var request = new AgentProcessRequest(
            AgentKind.Claude,
            options.WorkingDirectory,
            BuildArguments(sessionId, options),
            options.IsGeneral,
            options.EnvironmentVariables);
        var agent = await launcher.StartAsync(request, cancellationToken: cancellationToken);
        lock (gate)
        {
            process = agent;
        }

        _ = ReadOutputAsync(agent);
        try
        {
            await SendControlAsync(new JsonObject { ["subtype"] = "initialize" }, cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }

        return new AgentSessionStarted(sessionId, agent.ProcessId);
    }

    public async Task ChangeModeAsync(AgentPermissionProfile profile, CancellationToken cancellationToken = default)
    {
        var result = await SendControlAsync(new JsonObject
        {
            ["subtype"] = "set_permission_mode", ["mode"] = PermissionMode(profile)
        }, cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        // Claude normalizes manual to default (2.1.287). Never infer success from an empty response.
        var expected = profile == AgentPermissionProfile.Manual ? "default" : PermissionMode(profile);
        if (GetString(result, "mode") != expected)
            throw new AgentModeUnconfirmedException("O Claude não confirmou o modo solicitado.");
    }

    public async Task StartTurnAsync(AgentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
        // Defense in depth (#128): the registry refuses it first; nothing is written and no turn starts here.
        if (AgentInput.StartsWithCommand(input.Text))
            throw new AgentInputRejectedException(AgentInput.CommandRefusal(AgentKind.Claude));
        var agent = RequireOpen();
        var content = await ContentAsync(input, cancellationToken);
        lock (gate)
        {
            interruptRequested = false;
        }

        // Written before the message, so everything the turn produces comes after it.
        await events.Writer.WriteAsync(new TurnStartedEvent(), cancellationToken);
        await WriteAsync(agent, new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content }
        }, cancellationToken);
    }

    // The only path that writes a slash command (AD-32): the CLI confirms /clear with conversation_reset and then a result
    // with the new session_id. Nothing of it reaches the conversation as a turn.
    public async Task<AgentContextCleared> ClearContextAsync(CancellationToken cancellationToken = default)
    {
        var agent = RequireOpen();
        var clear = new ContextOperation();
        lock (gate)
        {
            if (operation is not null || tools.Count != 0 || pendingControls.Count != 0)
                throw new AgentContextUnchangedException("A sessão do Claude não está ociosa; a conversa não foi limpa.");
            operation = clear;
        }

        try
        {
            await WriteAsync(agent, new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "/clear" }
            }, cancellationToken);
            var result = await clear.Result.Task.WaitAsync(clearTimeout, cancellationToken);
            if (!clear.ResetSeen)
                throw new AgentContextUnchangedException("O Claude não confirmou a limpeza; a conversa anterior continua.");
            if (GetString(result, "subtype") != "success" || result["is_error"]?.GetValueKind() == JsonValueKind.True)
                throw new AgentProtocolException("O Claude reiniciou a conversa mas não concluiu o /clear.");
            return new AgentContextCleared(GetString(result, "session_id") ?? clear.SessionId
                ?? throw new AgentProtocolException("O Claude limpou a conversa sem informar o novo session_id."));
        }
        finally
        {
            lock (gate)
            {
                if (operation == clear) operation = null;
            }
        }
    }

    // /compact (#121, AD-32): confirmed only by compact_boundary, which carries the CLI's own token counts and keeps the
    // session_id. Without a boundary the result means there was nothing to compact, or the compaction failed with the
    // history intact. Cancelling or running out of time interrupts it upstream and waits for the CLI to say so.
    public async Task<AgentContextCompacted> CompactContextAsync(CancellationToken cancellationToken = default)
    {
        var agent = RequireOpen();
        var compact = new ContextOperation();
        lock (gate)
        {
            if (operation is not null || tools.Count != 0 || pendingControls.Count != 0)
                throw new AgentContextUnchangedException("A sessão do Claude não está ociosa; a conversa não foi compactada.");
            operation = compact;
        }

        try
        {
            await WriteAsync(agent, new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "/compact" }
            }, cancellationToken);
            try
            {
                await compact.Result.Task.WaitAsync(compactTimeout, cancellationToken);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                await WriteAsync(agent, new JsonObject
                {
                    ["type"] = "control_request",
                    ["request_id"] = NextControlId(),
                    ["request"] = new JsonObject { ["subtype"] = "interrupt" }
                }, CancellationToken.None);
                try
                {
                    await compact.Result.Task.WaitAsync(InterruptedOperationGrace, CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException("O Claude não encerrou a compactação interrompida.");
                }

                if (!compact.Compacted)
                    throw new AgentContextUnchangedException(exception is TimeoutException
                        ? "A compactação passou do limite e foi interrompida; a conversa anterior foi mantida."
                        : "A compactação foi cancelada; a conversa anterior foi mantida.");
            }

            if (compact.Compacted) return new AgentContextCompacted(compact.PreTokens, compact.PostTokens);
            throw new AgentContextUnchangedException(compact.CompactFailed
                ? $"O Claude não concluiu a compactação ({compact.CompactError ?? "erro desconhecido"}); a conversa " +
                  "anterior foi mantida."
                : "Não havia o que compactar: a conversa ainda não tem mensagens suficientes.");
        }
        finally
        {
            lock (gate)
            {
                if (operation == compact) operation = null;
            }
        }
    }

    // Text only stays a plain string. Images go as base64 image blocks, each after a label with its position and name,
    // and the user's text last (#93 spike, AD-29).
    private static async Task<JsonNode> ContentAsync(AgentInput input, CancellationToken cancellationToken)
    {
        if (input.Attachments.Count == 0) return input.Text;
        var content = new JsonArray();
        for (var index = 0; index < input.Attachments.Count; index++)
        {
            var image = input.Attachments[index];
            if (image.Kind != AttachmentKind.Image)
                throw new ArgumentException("O Claude só recebe imagens como anexo.", nameof(input));
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"Imagem {index + 1}" + (image.Name is null ? ":" : $" ({image.Name}):")
            });
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(image.Path, cancellationToken))
                }
            });
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = input.Text });
        return content;
    }

    public Task SteerAsync(AgentInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("O Claude não tem steer nativo: interrompa o turno e envie a mensagem.");

    // The turn ends with a result that the driver reports as Interrupted; the process keeps serving the session.
    public Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        var agent = RequireOpen();
        lock (gate)
        {
            interruptRequested = true;
            pendingControls.Clear();
        }

        return WriteAsync(agent, new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = NextControlId(),
            ["request"] = new JsonObject { ["subtype"] = "interrupt" }
        }, cancellationToken);
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
        lock (gate)
        {
            if (!pendingControls.TryGetValue(upstreamRequestId, out var pending))
            {
                throw new InvalidOperationException($"A solicitação {upstreamRequestId} não está pendente no Claude.");
            }

            result = (pending.Questions, response) switch
            {
                (null, AgentApprovalResponse approval) => ApprovalResult(pending, approval),
                ({ } questions, AgentInputResponse answers) => InputResult(pending, questions, answers),
                _ => throw new ArgumentException(
                    $"A solicitação {upstreamRequestId} não aceita esse tipo de resposta.", nameof(response))
            };
            pendingControls.Remove(upstreamRequestId);
        }

        return WriteAsync(agent, new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = upstreamRequestId,
                ["response"] = result
            }
        }, cancellationToken);
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
            // Claude ends the session when stdin reaches EOF; StopAsync kills the tree after the grace period.
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

    private static List<string> BuildArguments(string sessionId, AgentSessionStartOptions options)
    {
        // The prompt never goes on the command line: every turn is a JSON message on stdin.
        List<string> arguments =
        [
            "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--session-id", sessionId,
            "--permission-mode", PermissionMode(options.Profile),
            // Without it Claude denies escalations itself instead of asking the host.
            "--permission-prompt-tool", "stdio"
        ];
        if (options.IsGeneral)
        {
            // Same restrictions as the one-shot General Mode, plus AskUserQuestion so the agent can ask the user.
            arguments.AddRange(["--restricted", "--strict-mcp-config", "--tools", "Read,Write,Edit,AskUserQuestion"]);
        }

        // Without a selection the CLI picks its own default model (#77).
        if (options.ModelSelection?.Model is { } model) arguments.AddRange(["--model", model]);
        if (options.ModelSelection?.Effort is { } effort) arguments.AddRange(["--effort", effort]);
        return arguments;
    }

    private static string PermissionMode(AgentPermissionProfile profile) => profile switch
    {
        AgentPermissionProfile.Manual => "manual",
        AgentPermissionProfile.Auto => "auto",
        AgentPermissionProfile.Plan => "plan",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Perfil de permissão desconhecido.")
    };

    private static JsonObject ApprovalResult(PendingControl pending, AgentApprovalResponse approval)
    {
        if (approval.Decision == AgentApprovalDecision.Deny)
        {
            return new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = string.IsNullOrWhiteSpace(approval.Reason) ? "Negado pelo usuário." : approval.Reason
            };
        }

        var result = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = pending.Input.DeepClone() };
        // "For the session" applies only the suggestions scoped to the session (e.g. setMode acceptEdits); the
        // persistent ones (userSettings, projectSettings, localSettings) were dropped when the request arrived.
        if (approval.Decision == AgentApprovalDecision.ApproveForSession && pending.Suggestions is { Count: > 0 })
        {
            result["updatedPermissions"] = pending.Suggestions.DeepClone();
        }

        return result;
    }

    // /approve-session must never write a permission that outlives the session.
    private static JsonArray SessionSuggestions(JsonArray? suggestions) =>
        new((suggestions ?? [])
            .OfType<JsonObject>()
            .Where(suggestion => GetString(suggestion, "destination") == "session")
            .Select(suggestion => (JsonNode?)suggestion.DeepClone())
            .ToArray());

    // AskUserQuestion takes the answers inside updatedInput, keyed by the question text.
    private static JsonObject InputResult(
        PendingControl pending,
        IReadOnlyList<AgentQuestion> questions,
        AgentInputResponse response)
    {
        var answers = new JsonObject();
        foreach (var (id, answer) in response.Answers)
        {
            var question = questions.FirstOrDefault(candidate => candidate.Id == id)
                           ?? throw new ArgumentException($"A pergunta {id} não existe nesta solicitação.",
                               nameof(response));
            answers[question.Text] = answer;
        }

        var input = (JsonObject)pending.Input.DeepClone();
        input["answers"] = answers;
        return new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input };
    }

    private async Task ReadOutputAsync(InteractiveAgentProcess agent)
    {
        try
        {
            await foreach (var line in agent.Output.ReadAllAsync(lifetime.Token))
            {
                // stderr carries diagnostics, not protocol messages.
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
                $"O processo do Claude encerrou inesperadamente (código {exit.ExitCode})."));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            End(null);
        }
        catch (Exception exception)
        {
            End(exception as AgentProtocolException
                ?? new AgentProtocolException("Falha ao ler a saída do Claude.", exception));
            agent.Kill();
        }
    }

    private static JsonObject Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject
                   ?? throw new AgentProtocolException("O Claude enviou uma mensagem stream-json que não é um objeto.");
        }
        catch (JsonException exception)
        {
            throw new AgentProtocolException("O Claude enviou uma linha fora do protocolo stream-json.", exception);
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        ContextOperation? current;
        lock (gate) current = operation;
        if (current is not null && HandleOperation(current, message)) return;
        switch (GetString(message, "type"))
        {
            case "control_response":
                HandleControlResponse(message["response"] as JsonObject);
                break;
            case "control_request":
                await HandleControlRequestAsync(message);
                break;
            case "control_cancel_request":
                lock (gate)
                {
                    pendingControls.Remove(GetString(message, "request_id") ?? "");
                }

                break;
            case "stream_event":
                await HandleStreamEventAsync(message["event"] as JsonObject);
                break;
            case "assistant":
                await HandleAssistantAsync(message["message"] as JsonObject);
                break;
            case "user":
                await HandleToolResultsAsync(message["message"] as JsonObject);
                break;
            case "result":
                await HandleResultAsync(message);
                break;
            // system (init, status), rate_limit_event and future types carry nothing the session needs.
        }
    }

    // While a context operation runs, its reset, init and result belong to it and nothing becomes a conversation event.
    // Control messages (responses, cancellations) keep their normal path.
    private static bool HandleOperation(ContextOperation current, JsonObject message)
    {
        switch (GetString(message, "type"))
        {
            case "conversation_reset" when GetString(message, "trigger") == "clear":
                current.ResetSeen = true;
                return true;
            case "system" when GetString(message, "subtype") == "init":
                current.SessionId = GetString(message, "session_id");
                return true;
            case "system" when GetString(message, "subtype") == "compact_boundary":
                current.Compacted = true;
                current.PreTokens = TokenCount(message["compact_metadata"]?["pre_tokens"]);
                current.PostTokens = TokenCount(message["compact_metadata"]?["post_tokens"]);
                return true;
            case "system" when GetString(message, "subtype") == "status" &&
                               GetString(message, "compact_result") == "failed":
                current.CompactFailed = true;
                current.CompactError = GetString(message, "compact_error");
                return true;
            case "result":
                current.Result.TrySetResult(message);
                return true;
            case "assistant" or "user" or "stream_event" or "system" or "conversation_reset":
                return true;
            default:
                return false;
        }
    }

    private void HandleControlResponse(JsonObject? response)
    {
        var requestId = GetString(response, "request_id");
        TaskCompletionSource<JsonObject>? waiter;
        lock (gate)
        {
            if (requestId is null || !awaitingResponses.Remove(requestId, out waiter))
            {
                return;
            }
        }

        if (GetString(response, "subtype") == "success")
        {
            waiter.TrySetResult(response!["response"] as JsonObject ?? []);
        }
        else
        {
            waiter.TrySetException(new AgentProtocolException(
                $"O Claude recusou a solicitação de controle: {GetString(response, "error") ?? "erro desconhecido"}."));
        }
    }

    private async Task HandleControlRequestAsync(JsonObject message)
    {
        var requestId = GetString(message, "request_id")
                        ?? throw new AgentProtocolException("O Claude enviou uma solicitação de controle sem id.");
        var request = message["request"] as JsonObject;
        if (GetString(request, "subtype") != "can_use_tool")
        {
            // Nothing else is enabled by the driver's flags; answering keeps Claude from waiting forever.
            await WriteAsync(RequireOpen(), new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "error",
                    ["request_id"] = requestId,
                    ["error"] = "Solicitação não suportada pelo D.A.N.T.E."
                }
            }, lifetime.Token);
            return;
        }

        var toolName = GetString(request, "tool_name") ?? "";
        var input = request!["input"] as JsonObject ?? [];
        AgentEvent requested;
        PendingControl pending;
        if (toolName == AskUserQuestionTool)
        {
            var questions = (input["questions"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select((question, index) => new AgentQuestion(
                    $"q{index + 1}",
                    GetString(question, "question") ?? "",
                    (question["options"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(option => GetString(option, "label") ?? "")
                    .ToArray()))
                .ToArray();
            pending = new PendingControl(input, null, questions);
            requested = new UserInputRequestedEvent(requestId, questions);
        }
        else
        {
            pending = new PendingControl(
                input, SessionSuggestions(request["permission_suggestions"] as JsonArray), null);
            requested = new ApprovalRequestedEvent(
                requestId, ToolKind(toolName), Describe(toolName, input), GetString(input, "description"))
            { CanApproveForSession = pending.Suggestions is { Count: > 0 } };
        }

        // Registered before the event leaves, so an immediate answer finds it.
        lock (gate)
        {
            pendingControls[requestId] = pending;
        }

        await EmitAsync(requested);
    }

    private async Task HandleStreamEventAsync(JsonObject? streamEvent)
    {
        switch (GetString(streamEvent, "type"))
        {
            case "message_start":
                currentMessageId = GetString(streamEvent!["message"] as JsonObject, "id");
                break;
            case "content_block_delta" when streamEvent!["delta"] is JsonObject delta &&
                                            GetString(delta, "type") == "text_delta" &&
                                            GetString(delta, "text") is { Length: > 0 } text:
                await EmitAsync(new MessageDeltaEvent(currentMessageId ?? "", text));
                break;
        }
    }

    private async Task HandleAssistantAsync(JsonObject? message)
    {
        var messageId = GetString(message, "id") ?? "";
        foreach (var block in (message?["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { Length: > 0 } text:
                    await EmitAsync(new MessageCompletedEvent(messageId, text));
                    break;
                case "tool_use" when GetString(block, "id") is { } toolId &&
                                     GetString(block, "name") is { } name && name != AskUserQuestionTool:
                    // AskUserQuestion is reported as a user input request instead of a tool.
                    var input = block["input"] as JsonObject ?? [];
                    var kind = ToolKind(name);
                    lock (gate)
                    {
                        tools[toolId] = new ToolUse(kind, kind == AgentToolKind.FileChange ? FilePath(input) : null);
                    }

                    await EmitAsync(new ToolStartedEvent(toolId, kind, Describe(name, input)));
                    break;
            }
        }
    }

    private async Task HandleToolResultsAsync(JsonObject? message)
    {
        foreach (var block in (message?["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (GetString(block, "type") != "tool_result" || GetString(block, "tool_use_id") is not { } toolId)
            {
                continue;
            }

            ToolUse? tool;
            lock (gate)
            {
                tools.Remove(toolId, out tool);
            }

            if (tool is null)
            {
                continue;
            }

            var succeeded = block["is_error"]?.GetValueKind() != JsonValueKind.True;
            await EmitAsync(new ToolCompletedEvent(toolId, tool.Kind, succeeded, ContentText(block["content"])));
            if (succeeded && tool.Path is { } path)
            {
                await EmitAsync(new FileChangeEvent([path]));
            }
        }
    }

    private async Task HandleResultAsync(JsonObject result)
    {
        bool interrupted;
        lock (gate)
        {
            interrupted = interruptRequested;
            interruptRequested = false;
            pendingControls.Clear();
            tools.Clear();
        }

        var succeeded = GetString(result, "subtype") == "success" &&
                        result["is_error"]?.GetValueKind() != JsonValueKind.True;
        if (succeeded)
        {
            await EmitAsync(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        }
        else if (interrupted)
        {
            await EmitAsync(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
        }
        else
        {
            var errors = (result["errors"] as JsonArray ?? []).Select(error => error?.ToString())
                .Where(error => !string.IsNullOrWhiteSpace(error));
            var error = GetString(result, "result") is { Length: > 0 } text
                ? text
                : string.Join("; ", errors) is { Length: > 0 } joined ? joined : GetString(result, "subtype");
            await EmitAsync(new TurnCompletedEvent(AgentTurnOutcome.Failed, error));
        }
    }

    private async Task<JsonObject> SendControlAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var agent = RequireOpen();
        var requestId = NextControlId();
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (ended)
            {
                throw new AgentProtocolException("A sessão do Claude já foi encerrada.");
            }

            awaitingResponses[requestId] = waiter;
        }

        await WriteAsync(agent, new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = request
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
                throw new InvalidOperationException("A sessão do Claude ainda não foi iniciada.");
            }

            if (closing || ended)
            {
                throw new InvalidOperationException("A sessão do Claude está encerrada.");
            }

            return process;
        }
    }

    private string NextControlId() => $"dante-{Interlocked.Increment(ref nextControlId)}";

    // Ends the event stream once; requests still waiting for Claude fail with the same error.
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
            if (operation is not null) waiters = [.. waiters, operation.Result];
            awaitingResponses.Clear();
            pendingControls.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetException(error ?? new AgentProtocolException("A sessão do Claude foi encerrada."));
        }

        events.Writer.TryComplete(error);
    }

    private static AgentToolKind ToolKind(string toolName) => toolName switch
    {
        "Bash" => AgentToolKind.Command,
        "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => AgentToolKind.FileChange,
        _ => AgentToolKind.Tool
    };

    private static string Describe(string toolName, JsonObject input) => toolName switch
    {
        "Bash" => GetString(input, "command") ?? toolName,
        _ when FilePath(input) is { } path => $"{toolName} {path}",
        _ when GetString(input, "url") is { } url => $"{toolName} {url}",
        _ => toolName
    };

    private static string? FilePath(JsonObject input) =>
        GetString(input, "file_path") ?? GetString(input, "notebook_path");

    private static string? ContentText(JsonNode? content) => content switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Select(block => GetString(block, "text"))
            .Where(text => text is not null)),
        _ => null
    };

    private static string? GetString(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    // Questions is null for approvals; Suggestions are the permission updates Claude offered for the session.
    private sealed record PendingControl(JsonObject Input, JsonArray? Suggestions, IReadOnlyList<AgentQuestion>? Questions);

    private sealed record ToolUse(AgentToolKind Kind, string? Path);

    private static int? TokenCount(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0 ? count : null;

    private sealed class ContextOperation
    {
        public TaskCompletionSource<JsonObject> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ResetSeen { get; set; }
        public string? SessionId { get; set; }
        public bool Compacted { get; set; }
        public int? PreTokens { get; set; }
        public int? PostTokens { get; set; }
        public bool CompactFailed { get; set; }
        public string? CompactError { get; set; }
    }
}
