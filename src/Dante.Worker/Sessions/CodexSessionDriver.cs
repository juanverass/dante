using Dante.Worker.Coordenacao;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dante.Application.Anexos;
using Dante.Infrastructure.Agentes;

namespace Dante.Worker.Sessions;

// Codex over `codex app-server --listen stdio://` (AD-15, AD-19): JSON-RPC in JSONL on one process per session, with
// one ephemeral thread per D.A.N.T.E. session. Approvals and user input are server requests answered by id;
// steer and interrupt are client requests on the active turn.
// runtimeDoCodex: diretório do executável real do Codex (que o sandbox reexecuta), o único caminho fora da worktree que a
// revisão confinada lê além do mínimo de runtime (#308). Nulo: resolvido do PATH; sem ele, a revisão é recusada.
public sealed class CodexSessionDriver(
    IInteractiveAgentProcessLauncher launcher,
    TimeSpan? contextTimeout = null,
    TimeSpan? compactTimeout = null,
    Func<string?>? runtimeDoCodex = null) : IAgentSessionDriver
{
    private const int EventCapacity = 256;
    // thread/start answered in under a second in #119.
    private readonly TimeSpan clearTimeout = contextTimeout ?? TimeSpan.FromSeconds(30);
    // A compaction summarizes the thread with the model (9–32 s on short threads in #119).
    private readonly TimeSpan compactTimeout = compactTimeout ?? TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InterruptedOperationGrace = TimeSpan.FromSeconds(30);
    private const string UserInputMethod = "item/tool/requestUserInput";
    // MCP tool approval of the app-server (0.159.3): an elicitation with _meta.codex_approval_kind = mcp_tool_call (#224).
    private const string ElicitationMethod = "mcpServer/elicitation/request";
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
    // A compaction in flight (#121): its turn is the driver's, not a turn of the conversation.
    private Compaction? compaction;
    private string? model;
    private string? effort;
    private string? activeTurnId;
    private string? completedTurnId;
    // Last thread/tokenUsage/updated of the running turn (#148), attached to its completion.
    private AgentTokenUsage? turnUsage;
    private AgentPermissionProfile profile;
    private AgentPermissionProfile? pendingProfile;
    private TaskCompletionSource? modeConfirmation;
    private string? workingDirectory;
    private string conversationInstructions = "";
    // Checkouts of the linked repositories (AD-58): writable roots of every workspace-write thread and mode switch.
    private IReadOnlyList<string> writableRoots = [];
    private string? effectiveEffort;
    // Confined write step (#307): fixed policy for the whole session, no mode switch and no clear.
    private bool confinada;
    // Confined review (#308): the only MCP approval accepted is dante_coordenacao.entregar_resultado.
    private bool revisaoConfinada;
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

        if (options.EscritaConfinada && (options.Profile == AgentPermissionProfile.Plan || options.IsGeneral ||
                                         options.ToolServers is { Count: > 0 } || options.LeituraConfinada))
            throw new AgentProtocolException("Sessão de escrita confinada inválida; nada foi iniciado.");
        // Confined review (#308): plan, in a repository, only the dante_coordenacao server, no extra roots.
        if (options.LeituraConfinada && (options.Profile != AgentPermissionProfile.Plan || options.IsGeneral ||
                                         options.AdditionalDirectories is { Count: > 0 } ||
                                         (options.ToolServers ?? []).Any(server => server.Name != ServidorMcpDeCoordenacao.Nome)))
            throw new AgentProtocolException("Sessão de revisão confinada inválida; nada foi iniciado.");
        var naoConfiaveis = options.ProjetosNaoConfiaveis ?? [];
        // A main checkout is never the working directory nor a writable root of a confined session.
        if (options.EscritaConfinada && naoConfiaveis.Any(projeto =>
                string.Equals(projeto, options.WorkingDirectory, StringComparison.Ordinal) ||
                (options.AdditionalDirectories ?? []).Contains(projeto, StringComparer.Ordinal)))
            throw new AgentProtocolException("Sessão de escrita confinada com checkout principal gravável; nada foi iniciado.");
        if (options.LeituraConfinada && naoConfiaveis.Contains(options.WorkingDirectory, StringComparer.Ordinal))
            throw new AgentProtocolException("Sessão de revisão confinada no checkout principal; nada foi iniciado.");
        string? runtime = null;
        if (options.LeituraConfinada && (runtime = (runtimeDoCodex ?? RuntimeDoCodexNoPath)()) is null)
            throw new AgentProtocolException(
                "Executável do Codex não localizado para o confinamento da revisão; nada foi iniciado.");
        var (approvalPolicy, sandbox) = options.EscritaConfinada ? ConfinedPolicy : Policy(options.Profile);
        var approvalsReviewer = options.Profile == AgentPermissionProfile.Auto ? "auto_review" : "user";
        var roots = DiretoriosVinculados.Validar(options.AdditionalDirectories, options.IsGeneral).ToArray();
        // The prompt never goes on the command line: turns are JSON-RPC requests on stdin. A confined session also
        // empties the MCP servers of the user's configuration: they would run outside the sandbox.
        var request = new AgentProcessRequest(
            AgentKind.Codex,
            options.WorkingDirectory,
            // Confined review: the user's servers are emptied first and only dante_coordenacao is declared after it.
            ["app-server", "--listen", "stdio://",
                .. options.EscritaConfinada || options.LeituraConfinada ? (string[])["-c", "mcp_servers={}"] : [],
                .. McpConfig(options.ToolServers),
                .. options.LeituraConfinada ? ConfinamentoDaRevisao(options.WorkingDirectory, runtime!) : []],
            options.IsGeneral,
            options.EnvironmentVariables);
        var agent = await launcher.StartAsync(request, cancellationToken: cancellationToken);
        lock (gate)
        {
            process = agent;
            profile = options.Profile;
            workingDirectory = options.WorkingDirectory;
            writableRoots = roots;
            confinada = options.EscritaConfinada || options.LeituraConfinada;
            revisaoConfinada = options.LeituraConfinada;
            effort = options.ModelSelection?.Effort;
            conversationInstructions = ContratoDaConversaParaAgentes.ParaSessao(options.ToolServers,
                options.ContextInstructions);
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
                ["approvalPolicy"] = options.LeituraConfinada ? PoliticaDaRevisao() : approvalPolicy,
                ["approvalsReviewer"] = approvalsReviewer,
                ["developerInstructions"] = conversationInstructions,
                // D.A.N.T.E. sessions live only while the Worker runs (Epic #60).
                ["ephemeral"] = true
            };
            // The confined review takes its filesystem policy from the permission profile (sandbox_mode and
            // default_permissions cannot both be set).
            if (!options.LeituraConfinada) threadParameters["sandbox"] = sandbox;
            // Without a selection the CLI picks its own default model (#77); thread/start reports it either way.
            if (options.ModelSelection?.Model is { } selectedModel) threadParameters["model"] = selectedModel;
            if (options.EscritaConfinada) AddConfinement(threadParameters, roots, naoConfiaveis);
            else if (options.LeituraConfinada) AddReadConfinement(threadParameters, naoConfiaveis);
            else AddWritableRoots(threadParameters, roots);
            var thread = await SendRequestAsync("thread/start", threadParameters, cancellationToken);
            // The effective policy reported by the thread, not the request, decides (#307): anything other than exactly
            // the confined sandbox refuses the session before the first turn.
            if (options.EscritaConfinada && !ConfirmsConfinement(thread, options.WorkingDirectory, roots))
                throw new AgentProtocolException(
                    "O Codex não confirmou o confinamento de escrita (sandbox só nas worktrees, sem /tmp, sem rede e sem " +
                    "aprovações); nada foi enviado ao agente.");
            if (options.LeituraConfinada && !ConfirmsReadConfinement(thread, options.WorkingDirectory))
                throw new AgentProtocolException(
                    "O Codex não confirmou o confinamento da revisão (sandbox somente leitura, sem rede e sem aprovações); " +
                    "nada foi enviado ao agente.");
            // A session that cannot write to the linked repositories would fail the task halfway (AD-58).
            if (!ConfirmsWritableRoots(thread, options.Profile, roots))
                throw new AgentProtocolException("O Codex não confirmou o acesso aos repositórios vinculados. Atualize a CLI ou remova o vínculo.");
            // Do not silently run auto without the reviewer requested by the user.
            if (options.Profile == AgentPermissionProfile.Auto &&
                (GetString(thread, "approvalPolicy") != approvalPolicy ||
                 GetString(thread, "approvalsReviewer") != approvalsReviewer))
                throw new AgentProtocolException("O Codex não confirmou a revisão automática de aprovações. Atualize a CLI ou use uma sessão manual.");
            var id = GetString(thread["thread"] as JsonObject, "id")
                     ?? throw new AgentProtocolException("O Codex iniciou a thread sem id.");
            lock (gate)
            {
                threadId = id;
                model = GetString(thread, "model");
                effectiveEffort = options.ModelSelection?.Effort ?? GetString(thread, "reasoningEffort");
            }

            return new AgentSessionStarted(id, agent.ProcessId, GetString(thread, "model"), agent.UnidadeDeContencao);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public Task ChangeModeAsync(AgentPermissionProfile requested, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Policy(requested);
        lock (gate)
        {
            _ = RequireThread();
            if (confinada) throw new AgentModeRejectedException("A sessão de escrita confinada não troca de modo.");
            if (activeTurnId is not null || pendingRequests.Count != 0)
                throw new InvalidOperationException("Aguarde o turno do Codex terminar antes de trocar o modo.");
            pendingProfile = requested == profile ? null : requested;
        }
        return Task.CompletedTask;
    }

    // Only with the session idle: a turn/start during an active turn is absorbed by it instead of queued (AD-16).
    // The app-server has no reset of a thread (#119, AD-32): the clear is a fresh ephemeral thread in the same process, with
    // the directory, model and effective policies of the session. The session moves to it only after thread/start
    // confirms it; until then, and on any failure, the previous thread is untouched and stays in use.
    public async Task<AgentContextCleared> ClearContextAsync(CancellationToken cancellationToken = default)
    {
        string previous;
        AgentPermissionProfile current;
        string? currentModel;
        string? directory;
        IReadOnlyList<string> roots;
        lock (gate)
        {
            previous = RequireThread();
            if (confinada) throw new AgentContextUnchangedException("A sessão de escrita confinada não limpa a conversa.");
            if (activeTurnId is not null || pendingRequests.Count != 0 || pendingProfile is not null)
                throw new AgentContextUnchangedException("A sessão do Codex não está ociosa; a conversa não foi limpa.");
            (current, currentModel, directory, roots) = (profile, model, workingDirectory, writableRoots);
        }

        var (approvalPolicy, sandbox) = Policy(current);
        var approvalsReviewer = current == AgentPermissionProfile.Auto ? "auto_review" : "user";
        var parameters = new JsonObject
        {
            ["cwd"] = directory,
            ["approvalPolicy"] = approvalPolicy,
            ["approvalsReviewer"] = approvalsReviewer,
            ["sandbox"] = sandbox,
            ["developerInstructions"] = conversationInstructions,
            ["ephemeral"] = true
        };
        // The model the thread reported, so a change of the CLI default does not slip into the same session.
        if (currentModel is not null) parameters["model"] = currentModel;
        AddWritableRoots(parameters, roots);
        JsonObject thread;
        try
        {
            thread = await SendRequestAsync("thread/start", parameters, cancellationToken)
                .WaitAsync(clearTimeout, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            throw new AgentContextUnchangedException("O Codex não abriu a conversa nova; a conversa anterior continua.");
        }

        var id = GetString(thread["thread"] as JsonObject, "id");
        if (id is null || (current == AgentPermissionProfile.Auto &&
                           (GetString(thread, "approvalPolicy") != approvalPolicy ||
                            GetString(thread, "approvalsReviewer") != approvalsReviewer)) ||
            !ConfirmsWritableRoots(thread, current, roots))
        {
            if (id is not null) await UnsubscribeQuietlyAsync(id);
            throw new AgentContextUnchangedException(
                "O Codex não confirmou a conversa nova com as mesmas políticas; a conversa anterior continua.");
        }

        lock (gate)
        {
            threadId = id;
            model = GetString(thread, "model") ?? model;
        }

        await UnsubscribeQuietlyAsync(previous);
        return new AgentContextCleared(id);
    }

    // Best effort: an abandoned ephemeral thread is unloaded by the server after a grace period anyway.
    private async Task UnsubscribeQuietlyAsync(string thread)
    {
        try
        {
            await SendRequestAsync("thread/unsubscribe", new JsonObject { ["threadId"] = thread }, lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException
                                              or OperationCanceledException or AgentProtocolException)
        {
        }
    }

    // thread/compact/start (#121, AD-32) answers {} at once; the compaction then runs as a turn of its own with a
    // contextCompaction item, on the same thread. That turn is correlated here and never reaches the session as a turn.
    // Completed with the item completed is success; interrupted or failed leaves the history intact. The app-server
    // reports no context size for it, so no reduction is returned.
    public async Task<AgentContextCompacted> CompactContextAsync(CancellationToken cancellationToken = default)
    {
        var compact = new Compaction();
        string thread;
        lock (gate)
        {
            thread = RequireThread();
            if (compaction is not null || activeTurnId is not null || pendingRequests.Count != 0)
                throw new AgentContextUnchangedException("A sessão do Codex não está ociosa; a conversa não foi compactada.");
            compaction = compact;
        }

        try
        {
            try
            {
                try
                {
                    await SendRequestAsync("thread/compact/start", new JsonObject { ["threadId"] = thread },
                        cancellationToken).WaitAsync(clearTimeout, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // Only an explicit RPC rejection proves that the request was not accepted.
                    throw new AgentContextUnchangedException(
                        "O Codex não iniciou a compactação; a conversa anterior foi mantida.");
                }

                await compact.Result.Task.WaitAsync(compactTimeout, cancellationToken);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // A missing ACK does not mean rejection: notifications can already confirm completion.
                // Otherwise keep consuming this operation's notifications until interrupt is confirmed.
                if (!compact.Result.Task.IsCompleted)
                {
                    var turn = await compact.Started.Task.WaitAsync(InterruptedOperationGrace, CancellationToken.None);
                    if (!compact.Result.Task.IsCompleted)
                    {
                        try
                        {
                            await SendRequestAsync("turn/interrupt",
                                new JsonObject { ["threadId"] = thread, ["turnId"] = turn }, CancellationToken.None)
                                .WaitAsync(InterruptedOperationGrace, CancellationToken.None);
                        }
                        catch (InvalidOperationException)
                        {
                            // It may have ended between the timeout and the interrupt: await confirmation below.
                        }
                    }
                }

                // Missing start/completion or failed transport remains uncertain and reaches the registry as such.
                await compact.Result.Task.WaitAsync(InterruptedOperationGrace, CancellationToken.None);
                if (!compact.Succeeded)
                    throw new AgentContextUnchangedException(exception is TimeoutException
                        ? "A compactação passou do limite e foi interrompida; a conversa anterior foi mantida."
                        : "A compactação foi cancelada; a conversa anterior foi mantida.");
            }

            if (compact.Succeeded) return new AgentContextCompacted();
            throw new AgentContextUnchangedException(
                $"O Codex não concluiu a compactação ({compact.Error ?? compact.Status ?? "sem detalhes"}); a conversa " +
                "anterior foi mantida.");
        }
        finally
        {
            lock (gate)
            {
                if (compaction == compact) compaction = null;
            }
        }
    }

    // While a compaction runs, every notification of the thread belongs to its turn and nothing becomes a session event.
    private static void HandleCompaction(Compaction compact, string method, JsonObject parameters)
    {
        switch (method)
        {
            case "turn/started" when GetString(parameters["turn"] as JsonObject, "id") is { } turn:
                compact.Started.TrySetResult(turn);
                break;
            case "item/completed" when GetString(parameters["item"] as JsonObject, "type") == "contextCompaction":
                compact.ItemCompleted = true;
                break;
            case "error" when parameters["error"] is JsonObject error:
                compact.Error = GetString(error, "message");
                break;
            case "turn/completed" when parameters["turn"] is JsonObject turn:
                compact.Status = GetString(turn, "status");
                compact.Error ??= GetString(turn["error"] as JsonObject, "message");
                compact.Result.TrySetResult();
                break;
        }
    }

    public async Task StartTurnAsync(AgentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
        string thread;
        AgentPermissionProfile current;
        string? currentModel;
        TaskCompletionSource? confirmation;
        IReadOnlyList<string> roots;
        lock (gate)
        {
            thread = RequireThread();
            roots = writableRoots;
            current = pendingProfile ?? profile;
            confirmation = pendingProfile is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
            modeConfirmation = confirmation;
            currentModel = model;
        }

        var parameters = new JsonObject { ["threadId"] = thread, ["input"] = Input(input) };
        if (effort is not null) parameters["effort"] = effort;
        if (confirmation is not null)
        {
            parameters["approvalPolicy"] = Policy(current).ApprovalPolicy;
            parameters["approvalsReviewer"] = current == AgentPermissionProfile.Auto ? "auto_review" : "user";
            parameters["sandboxPolicy"] = current == AgentPermissionProfile.Plan
                ? new JsonObject { ["type"] = "readOnly" }
                : new JsonObject
                {
                    ["type"] = "workspaceWrite", ["writableRoots"] = Roots(roots),
                    ["networkAccess"] = false, ["excludeTmpdirEnvVar"] = false, ["excludeSlashTmp"] = false
                };
        }
        if (current == AgentPermissionProfile.Plan || confirmation is not null)
        {
            // The plan collaboration mode needs the model explicitly; thread/start reported it.
            parameters["collaborationMode"] = new JsonObject
            {
                ["mode"] = current == AgentPermissionProfile.Plan ? "plan" : "default",
                ["settings"] = new JsonObject { ["model"] = currentModel, ["developer_instructions"] = null }
            };
        }

        if (effort is not null && parameters["collaborationMode"]?["settings"] is JsonObject collaborationSettings)
            collaborationSettings["reasoning_effort"] = effort;

        JsonObject result;
        try
        {
            try
            {
                result = await SendRequestAsync("turn/start", parameters, cancellationToken);
            }
            catch (InvalidOperationException) when (confirmation is not null)
            {
                if (confirmation.Task.IsCompletedSuccessfully)
                    throw new AgentProtocolException("O Codex confirmou políticas novas mas recusou o turno; a sessão será encerrada.");
                lock (gate) pendingProfile = null;
                throw new AgentModeRejectedException("O Codex recusou a troca de modo; o turno não foi iniciado e o modo anterior foi mantido.");
            }
            if (confirmation is not null)
                await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        finally
        {
            lock (gate) modeConfirmation = null;
        }
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
            await WriteAsync(agent, Response(request.Id, request.Method switch
            {
                UserInputMethod => new JsonObject { ["answers"] = new JsonObject() },
                ElicitationMethod => new JsonObject { ["action"] = "cancel", ["content"] = null },
                _ => new JsonObject { ["decision"] = "cancel" }
            }), cancellationToken);
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
                (false, AgentApprovalResponse approval) when pending.Method == ElicitationMethod => new JsonObject
                {
                    ["action"] = approval.Decision == AgentApprovalDecision.Deny ? "decline" : "accept", ["content"] = null
                },
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

    // -c overrides that declare the D.A.N.T.E. tool servers to the app-server (#224); TOML accepts JSON strings and arrays.
    // Read-only tools are annotated readOnlyHint by the server, so Codex runs them without an approval.
    private static IEnumerable<string> McpConfig(IReadOnlyList<AgentToolServer>? servers)
    {
        foreach (var server in servers ?? [])
        {
            var prefix = $"mcp_servers.{server.Name}";
            yield return "-c";
            yield return $"{prefix}.command={JsonSerializer.Serialize(server.Command)}";
            yield return "-c";
            yield return $"{prefix}.args={JsonSerializer.Serialize(server.Arguments)}";
            yield return "-c";
            yield return $"{prefix}.env={{{string.Join(", ", server.Environment.Select(pair => $"{pair.Key} = {JsonSerializer.Serialize(pair.Value)}"))}}}";
            yield return "-c";
            yield return $"{prefix}.startup_timeout_sec=30";
        }
    }

    // "Allow the X MCP server to run tool \"y\"?" plus the tool parameters: what the user approves.
    private static string McpToolAction(JsonObject parameters, JsonObject meta)
    {
        var message = GetString(parameters, "message") ?? "";
        var start = message.IndexOf("tool \"", StringComparison.Ordinal);
        var end = start < 0 ? -1 : message.IndexOf('"', start + 6);
        var tool = start >= 0 && end > start ? message[(start + 6)..end] : "ferramenta";
        var arguments = meta["tool_params"]?.ToJsonString() ?? "{}";
        if (arguments.Length > 800) arguments = arguments[..800] + "…";
        return $"{GetString(parameters, "serverName") ?? "mcp"}.{tool} {arguments}";
    }

    // approvalPolicy and sandbox of thread/start. No profile reaches danger-full-access; that choice is #67's.
    private static (string ApprovalPolicy, string Sandbox) Policy(AgentPermissionProfile profile) => profile switch
    {
        AgentPermissionProfile.Manual => ("on-request", "workspace-write"),
        AgentPermissionProfile.Auto => ("on-request", "workspace-write"),
        AgentPermissionProfile.Plan => ("on-request", "read-only"),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Perfil de permissão desconhecido.")
    };

    // Confined write step (#307): workspace-write never asks (no interactive approval or escalation without the gateway
    // #311); failures inside the sandbox are returned to the model.
    private static readonly (string ApprovalPolicy, string Sandbox) ConfinedPolicy = ("never", "workspace-write");

    // The worktrees are the only writable roots besides the working directory; /tmp and TMPDIR are excluded and the
    // network is off. The shared Git directory of the repositories is outside every root, so it stays read-only:
    // commit, ref updates and the main checkout are denied by the sandbox, as the live test shows (codex-cli 0.166).
    // The repositories of the worktrees are declared untrusted: codex-cli 0.166 otherwise persists trust for the main
    // repository in ~/.codex/config.toml on thread/start (a write by the CLI itself, outside the sandbox) and would load
    // project config from it. The live evidence checks the user's config stays byte for byte the same.
    private static void AddConfinement(JsonObject parameters, IReadOnlyList<string> roots, IReadOnlyList<string> untrusted) =>
        parameters["config"] = new JsonObject
        {
            ["sandbox_workspace_write"] = new JsonObject
            {
                ["writable_roots"] = Roots(roots),
                ["exclude_slash_tmp"] = true,
                ["exclude_tmpdir_env_var"] = true,
                ["network_access"] = false
            },
            ["projects"] = new JsonObject(untrusted.Distinct(StringComparer.Ordinal).Select(project =>
                KeyValuePair.Create(project, (JsonNode?)new JsonObject { ["trust_level"] = "untrusted" })))
        };

    // Confined review step (#308, review do PR #317). The filesystem is confined by a permission profile enforced by the
    // OS sandbox (bubblewrap on Linux), not by read-only mode: the reviewer reads only the worktree, the minimal runtime
    // (":minimal") and the Codex executable directory (the sandbox re-executes it); home, other checkouts, credentials
    // and the rest of the host do not exist inside it. Writes and network are denied. Tools that act outside the
    // sandbox or widen the reach are switched off (account connectors, image viewer, sub-agents, goals, web search),
    // and the approval policy never escalates the sandbox: only MCP tool approvals reach the client, and the driver
    // accepts exactly dante_coordenacao.entregar_resultado.
    public const string PerfilDaRevisao = "dante_revisao";

    internal static IReadOnlyList<string> ConfinamentoDaRevisao(string worktree, string runtime)
    {
        if (!Path.IsPathFullyQualified(worktree) || !Path.IsPathFullyQualified(runtime) ||
            worktree.Any(char.IsControl) || runtime.Any(char.IsControl))
            throw new AgentProtocolException("Caminho inválido para o confinamento da revisão; nada foi iniciado.");
        return
        [
            "-c", "features.apps=false", "-c", "features.view_image=false", "-c", "features.multi_agent=false",
            "-c", "features.goals=false", "-c", "web_search=\"disabled\"",
            "-c", $"default_permissions=\"{PerfilDaRevisao}\"",
            "-c", $"permissions.{PerfilDaRevisao}.filesystem={{\":minimal\" = \"read\", " +
                $"{JsonSerializer.Serialize(worktree)} = \"read\", {JsonSerializer.Serialize(runtime)} = \"read\"}}"
        ];
    }

    // Approvals the review may raise: MCP tool calls only. Sandbox escalation, exec rules, skills and permission
    // requests are refused by the CLI itself (it never asks).
    internal static JsonObject PoliticaDaRevisao() => new()
    {
        ["granular"] = new JsonObject
        {
            ["sandbox_approval"] = false, ["rules"] = false, ["skill_approval"] = false, ["request_permissions"] = false,
            ["mcp_elicitations"] = true
        }
    };

    private static string? RuntimeDoCodexNoPath()
    {
        foreach (var diretorio in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidato = Path.Combine(diretorio, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (!File.Exists(candidato)) continue;
            // O sandbox reexecuta o executável pelo caminho real: links no meio do caminho (current → release) não valem.
            return Path.GetDirectoryName(CaminhoReal(candidato));
        }
        return null;
    }

    // Caminho canônico: cada componente que é link é substituído pelo destino (como realpath), com limite de saltos.
    internal static string CaminhoReal(string caminho)
    {
        var pendentes = new Queue<string>(Path.GetFullPath(caminho).Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries));
        var atual = Path.DirectorySeparatorChar.ToString();
        var saltos = 0;
        while (pendentes.TryDequeue(out var parte))
        {
            var proximo = Path.Combine(atual, parte);
            if (new FileInfo(proximo).LinkTarget is { } alvo)
            {
                if (++saltos > 40) throw new IOException("Links demais ao resolver o caminho do Codex.");
                var destino = Path.GetFullPath(Path.IsPathRooted(alvo) ? alvo : Path.Combine(atual, alvo));
                pendentes = new Queue<string>(destino.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(pendentes));
                atual = Path.DirectorySeparatorChar.ToString();
                continue;
            }
            atual = proximo;
        }
        return atual;
    }

    // The repository of the worktree stays untrusted, as in the confined write (no trust persisted, no project config).
    private static void AddReadConfinement(JsonObject parameters, IReadOnlyList<string> untrusted) =>
        parameters["config"] = new JsonObject
        {
            ["projects"] = new JsonObject(untrusted.Distinct(StringComparer.Ordinal).Select(project =>
                KeyValuePair.Create(project, (JsonNode?)new JsonObject { ["trust_level"] = "untrusted" })))
        };

    internal static JsonObject ConfinedReadSandboxPolicy() => new() { ["type"] = "readOnly", ["networkAccess"] = false };

    // The thread must report the review profile as active, the review approval policy, the requested cwd and a
    // read-only, network-less legacy summary of it.
    internal static bool ConfirmsReadConfinement(JsonObject thread, string workingDirectory) =>
        thread["approvalPolicy"] is JsonObject politica && JsonNode.DeepEquals(politica, PoliticaDaRevisao()) &&
        GetString(thread, "cwd") == workingDirectory &&
        thread["activePermissionProfile"] is JsonObject perfil && GetString(perfil, "id") == PerfilDaRevisao &&
        thread["sandbox"] is JsonObject sandbox && GetString(sandbox, "type") == "readOnly" &&
        (sandbox["networkAccess"] is null || IsFalse(sandbox, "networkAccess"));

    // Exactly the verdict tool of this session's coordination server, as the 0.166 app-server words it.
    internal static bool EntregaDoVereditoAutorizada(JsonObject parameters, string? thread) =>
        GetString(parameters, "serverName") == ServidorMcpDeCoordenacao.Nome &&
        GetString(parameters, "message") ==
            $"Allow the {ServidorMcpDeCoordenacao.Nome} MCP server to run tool \"{ServidorMcpDeCoordenacao.EntregarResultado}\"?" &&
        thread is not null && GetString(parameters, "threadId") == thread &&
        parameters["_meta"] is JsonObject meta && GetString(meta, "codex_approval_kind") == "mcp_tool_call";

    // The same confinement as a SandboxPolicy object (command/exec, turn/start): used by the live evidence that runs real
    // commands under it, so the policy proved is the one this driver confirms.
    internal static JsonObject ConfinedSandboxPolicy(IReadOnlyList<string> roots) => new()
    {
        ["type"] = "workspaceWrite",
        ["writableRoots"] = Roots(roots),
        ["networkAccess"] = false,
        ["excludeTmpdirEnvVar"] = true,
        ["excludeSlashTmp"] = true
    };

    internal static bool ConfirmsConfinement(JsonObject thread, string workingDirectory, IReadOnlyList<string> roots) =>
        GetString(thread, "approvalPolicy") == ConfinedPolicy.ApprovalPolicy &&
        GetString(thread, "cwd") == workingDirectory &&
        thread["sandbox"] is JsonObject sandbox && GetString(sandbox, "type") == "workspaceWrite" &&
        SameRoots(sandbox["writableRoots"] as JsonArray, roots) &&
        IsTrue(sandbox, "excludeSlashTmp") && IsTrue(sandbox, "excludeTmpdirEnvVar") && IsFalse(sandbox, "networkAccess");

    private static bool IsTrue(JsonObject node, string name) =>
        node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    private static bool IsFalse(JsonObject node, string name) =>
        node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.False;

    // Linked repositories reach thread/start as the sandbox_workspace_write.writable_roots override (codex-cli 0.159.3).
    private static void AddWritableRoots(JsonObject parameters, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return;
        parameters["config"] = new JsonObject
        {
            ["sandbox_workspace_write"] = new JsonObject { ["writable_roots"] = Roots(roots) }
        };
    }

    // thread/start answers with the effective sandbox: without linked repositories nothing new is checked; with them a
    // workspace-write thread must list exactly those roots. A read-only (plan) thread writes nowhere.
    private static bool ConfirmsWritableRoots(JsonObject thread, AgentPermissionProfile requested,
        IReadOnlyList<string> roots) =>
        roots.Count == 0 || requested == AgentPermissionProfile.Plan ||
        (thread["sandbox"] is JsonObject sandbox && GetString(sandbox, "type") == "workspaceWrite" &&
         SameRoots(sandbox["writableRoots"] as JsonArray, roots));

    private static bool SameRoots(JsonArray? reported, IReadOnlyList<string> roots) =>
        reported is not null && reported.Count == roots.Count &&
        reported.Select(root => root?.GetValueKind() == JsonValueKind.String ? root.GetValue<string>() : null)
            .Order(StringComparer.Ordinal).SequenceEqual(roots.Order(StringComparer.Ordinal));

    private static JsonArray Roots(IReadOnlyList<string> roots) => new(roots.Select(root => (JsonNode?)root).ToArray());

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

    private bool RevisaoConfinada(out string? thread)
    {
        lock (gate)
        {
            thread = threadId;
            return revisaoConfinada;
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
            case ElicitationMethod when RevisaoConfinada(out var thread):
            {
                // Confined review (#308): no approval reaches a person; only the verdict tool of this session's
                // coordination server is accepted, anything else is declined.
                var aceita = EntregaDoVereditoAutorizada(parameters, thread);
                await WriteAsync(RequireOpen(), Response(id.DeepClone(), new JsonObject
                {
                    ["action"] = aceita ? "accept" : "decline", ["content"] = null
                }), lifetime.Token);
                return;
            }
            case ElicitationMethod when parameters["_meta"] is JsonObject meta &&
                                        GetString(meta, "codex_approval_kind") == "mcp_tool_call":
                bool planning;
                lock (gate) planning = profile == AgentPermissionProfile.Plan;
                if (planning)
                {
                    // Planning never changes anything outside the conversation: an MCP tool that writes is declined.
                    await WriteAsync(RequireOpen(), Response(id.DeepClone(), new JsonObject
                    {
                        ["action"] = "decline", ["content"] = null
                    }), lifetime.Token);
                    return;
                }

                pending = new PendingServerRequest(id.DeepClone(), method, []);
                requested = new ApprovalRequestedEvent(upstreamId, AgentToolKind.Tool, McpToolAction(parameters, meta));
                break;
            default:
                // Other elicitations, permission profiles, dynamic tools, auth refresh…: not enabled by the D.A.N.T.E.;
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
        // After a clear, whatever the previous thread still says is not part of the session (#120).
        Compaction? compact;
        lock (gate)
        {
            if (GetString(parameters, "threadId") is { } thread && threadId is not null && thread != threadId) return;
            compact = compaction;
        }

        if (compact is not null)
        {
            HandleCompaction(compact, method, parameters);
            return;
        }

        switch (method)
        {
            case "thread/settings/updated":
                await ConfirmModeAsync(parameters);
                break;
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
            case "thread/tokenUsage/updated" when parameters["tokenUsage"] is JsonObject tokenUsage &&
                                                 tokenUsage["last"] is JsonObject last:
                lock (gate)
                {
                    turnUsage = Usage(last);
                }

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

    private async Task ConfirmModeAsync(JsonObject parameters)
    {
        AgentPermissionProfile requested;
        TaskCompletionSource confirmation;
        lock (gate)
        {
            if (modeConfirmation is null || pendingProfile is null || GetString(parameters, "threadId") != threadId)
                return;
            requested = pendingProfile.Value;
            confirmation = modeConfirmation;
        }
        var settings = parameters["threadSettings"] as JsonObject;
        var sandbox = settings?["sandboxPolicy"] as JsonObject;
        var collaboration = settings?["collaborationMode"] as JsonObject;
        // A positive RPC response alone does not attest the policy. Compare the effective settings notification.
        if (sandbox is null || GetString(settings, "approvalPolicy") != Policy(requested).ApprovalPolicy ||
            GetString(settings, "approvalsReviewer") != (requested == AgentPermissionProfile.Auto ? "auto_review" : "user") ||
            GetString(sandbox, "type") != (requested == AgentPermissionProfile.Plan ? "readOnly" : "workspaceWrite") ||
            sandbox["networkAccess"] is not JsonValue network || network.GetValueKind() != JsonValueKind.False ||
            (requested != AgentPermissionProfile.Plan &&
                (!SameRoots(sandbox["writableRoots"] as JsonArray, writableRoots) ||
                 sandbox["excludeTmpdirEnvVar"]?.GetValue<bool>() != false || sandbox["excludeSlashTmp"]?.GetValue<bool>() != false)) ||
            GetString(collaboration, "mode") != (requested == AgentPermissionProfile.Plan ? "plan" : "default") ||
            GetString(settings, "cwd") != workingDirectory || GetString(settings, "model") != model ||
            GetString(settings, "effort") != effectiveEffort)
        {
            confirmation.TrySetException(new AgentProtocolException("O Codex não confirmou as políticas do novo modo; a sessão será encerrada."));
            return;
        }
        lock (gate)
        {
            profile = requested;
            pendingProfile = null;
        }
        // Settings are effective upstream before turn/started. Preserve that order for snapshots and delivery.
        await EmitAsync(new ModeAppliedEvent(requested));
        confirmation.TrySetResult();
    }

    private static ToolStartedEvent? ToolStarted(JsonObject item)
    {
        var itemId = GetString(item, "id") ?? "";
        return GetString(item, "type") switch
        {
            "commandExecution" => new ToolStartedEvent(itemId, AgentToolKind.Command, GetString(item, "command") ?? ""),
            "fileChange" => new ToolStartedEvent(itemId, AgentToolKind.FileChange, string.Join(", ", ChangedPaths(item))),
            "mcpToolCall" => ApresentacaoDeFerramentas.DoCodex(itemId, GetString(item, "server"), GetString(item, "tool"),
                $"{GetString(item, "server")}.{GetString(item, "tool")}"),
            "dynamicToolCall" => ApresentacaoDeFerramentas.DoCodex(itemId, null, GetString(item, "tool"), GetString(item, "tool") ?? ""),
            "webSearch" => ApresentacaoDeFerramentas.DoCodex(itemId, null, "webSearch", $"webSearch {GetString(item, "query")}"),
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
            // The saved path is the only artifact channel of the Codex session (#93 spike, AD-29).
            case "imageGeneration" when GetString(item, "savedPath") is { Length: > 0 } saved:
                await EmitAsync(new ArtifactProducedEvent(itemId, saved));
                break;
        }
    }

    // tokenUsage.last of the app-server: inputTokens already includes cachedInputTokens.
    private static AgentTokenUsage? Usage(JsonObject last)
    {
        long? Count(string name) => last[name] is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0
            ? count : null;
        return Count("inputTokens") is null && Count("outputTokens") is null ? null :
            new AgentTokenUsage(Count("inputTokens"), Count("outputTokens"), Count("cachedInputTokens"));
    }

    private async Task HandleTurnCompletedAsync(JsonObject? turn)
    {
        AgentTokenUsage? usage;
        lock (gate)
        {
            completedTurnId = GetString(turn, "id");
            activeTurnId = null;
            pendingRequests.Clear();
            usage = turnUsage;
            turnUsage = null;
        }

        await EmitAsync(GetString(turn, "status") switch
        {
            "completed" => new TurnCompletedEvent(AgentTurnOutcome.Completed, Usage: usage),
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
            compaction?.Result.TrySetException(error ?? new AgentProtocolException("A sessão do Codex foi encerrada."));
            compaction?.Started.TrySetException(error ?? new AgentProtocolException("A sessão do Codex foi encerrada."));
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

    private sealed class Compaction
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ItemCompleted { get; set; }
        public string? Status { get; set; }
        public string? Error { get; set; }
        public bool Succeeded => Status == "completed" && ItemCompleted;
    }
}
