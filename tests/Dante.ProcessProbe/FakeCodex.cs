using System.Text.Json.Nodes;

namespace Dante.ProcessProbe;

// Simulated `codex app-server --listen stdio://` (JSON-RPC in JSONL), shaped like codex-cli 0.157.1: responses have no
// "jsonrpc" field, like the real server. Each turn picks a scenario by its text: pong, config, command, edit, ask,
// slow, fail, warn, elicit, garbage, crash or model. model/list answers in two pages; account/read and
// account/rateLimits/read answer the quota query (#116).
internal static class FakeCodex
{
    public static int Run(string[] args)
    {
        var turns = 0;
        // Every thread/start opens a new thread (a clear, #120); turns are counted per thread, like a new context.
        var threads = 0;
        var threadId = "thread-1";
        string? previousThread = null;
        var unsubscribed = new List<string>();
        var nextServerRequest = 100;
        string? activeTurn = null;
        string? waitingScenario = null;
        long? waitingFor = null;
        JsonObject? threadParams = null;
        JsonObject? turnParams = null;

        void Send(JsonObject message) => Console.WriteLine(message.ToJsonString());

        void Notify(string method, JsonObject parameters) =>
            Send(new JsonObject { ["method"] = method, ["params"] = parameters });

        void Item(string method, JsonObject item) => Notify(method, new JsonObject
        {
            ["threadId"] = threadId, ["turnId"] = activeTurn, ["item"] = item
        });

        void Message(string text) => Item("item/completed", new JsonObject
        {
            ["type"] = "agentMessage", ["id"] = $"msg-{turns}-{text.Length}", ["text"] = text
        });

        void Complete(string status, string? error = null)
        {
            var turn = new JsonObject { ["id"] = activeTurn, ["items"] = new JsonArray(), ["status"] = status };
            if (error is not null)
            {
                turn["error"] = new JsonObject { ["message"] = error };
            }

            activeTurn = null;
            waitingFor = null;
            waitingScenario = null;
            Notify("turn/completed", new JsonObject { ["threadId"] = threadId, ["turn"] = turn });
        }

        void ServerRequest(string scenario, string method, JsonObject parameters)
        {
            waitingScenario = scenario;
            waitingFor = ++nextServerRequest;
            parameters["threadId"] = threadId;
            parameters["turnId"] = activeTurn;
            Send(new JsonObject { ["id"] = waitingFor, ["method"] = method, ["params"] = parameters });
        }

        while (Console.In.ReadLine() is { } line)
        {
            var message = JsonNode.Parse(line)!.AsObject();
            var method = (string?)message["method"];
            var id = message["id"]?.DeepClone();
            var parameters = message["params"] as JsonObject;

            void Reply(JsonObject result) => Send(new JsonObject { ["id"] = id, ["result"] = result });

            void Fail(string error) => Send(new JsonObject
            {
                ["id"] = id, ["error"] = new JsonObject { ["code"] = -32600, ["message"] = error }
            });

            if (method is null)
            {
                // A response to one of our server requests.
                if (id is null || (long?)id != waitingFor)
                {
                    continue;
                }

                var result = message["result"] as JsonObject;
                switch (waitingScenario)
                {
                    case "command":
                        var decision = (string?)result!["decision"];
                        var accepted = decision is "accept" or "acceptForSession";
                        Item("item/completed", new JsonObject
                        {
                            ["type"] = "commandExecution", ["id"] = "cmd-1", ["command"] = "dotnet test",
                            ["status"] = accepted ? "completed" : "declined",
                            ["exitCode"] = accepted ? 0 : null,
                            ["aggregatedOutput"] = accepted ? "ok" : null
                        });
                        Message($"decision:{decision}");
                        if (decision == "cancel")
                        {
                            // Left to the interrupt that follows.
                            waitingFor = null;
                            continue;
                        }

                        break;
                    case "edit":
                        Item("item/completed", new JsonObject
                        {
                            ["type"] = "fileChange", ["id"] = "fc-1",
                            ["status"] = (string?)result!["decision"] == "accept" ? "completed" : "declined",
                            ["changes"] = new JsonArray(new JsonObject
                            {
                                ["path"] = "a.txt", ["kind"] = new JsonObject { ["type"] = "add" }, ["diff"] = "+hi"
                            })
                        });
                        break;
                    case "ask":
                        Message("answers:" + result!["answers"]!.ToJsonString());
                        break;
                    case "elicit":
                        Message("rejected:" + (string?)message["error"]!["message"]);
                        break;
                }

                Complete("completed");
                continue;
            }

            switch (method)
            {
                case "initialize":
                    Reply(new JsonObject { ["userAgent"] = "fake-codex/0.157.1" });
                    break;
                case "initialized":
                    break;
                case "thread/start":
                    if (args.Contains("reject-thread"))
                    {
                        Fail("thread rejected");
                        break;
                    }

                    // Scenarios for a second thread/start (the clear): reject-clear, hang-clear.
                    if (threads > 0 && args.Contains("reject-clear"))
                    {
                        Fail("thread rejected");
                        break;
                    }
                    if (threads > 0 && args.Contains("hang-clear")) break;
                    threads++;
                    if (threads > 1) previousThread = threadId;
                    threadId = $"thread-{threads}";
                    turns = 0;
                    threadParams = parameters;
                    Reply(new JsonObject
                    {
                        ["thread"] = new JsonObject { ["id"] = threadId, ["cwd"] = (string?)parameters!["cwd"] },
                        ["approvalPolicy"] = (string?)parameters["approvalPolicy"],
                        ["approvalsReviewer"] = args.Contains("wrong-reviewer") ? "user" :
                            args.Contains("missing-reviewer") ? null : (string?)parameters["approvalsReviewer"],
                        ["model"] = (string?)parameters["model"] ?? "fake-model"
                    });
                    break;
                case "thread/compact/start":
                    // Shaped like codex-cli 0.159.3 (#119): {} at once, then a turn of its own with a contextCompaction
                    // item. Scenarios: compact-reject, compact-fail, compact-hang (until turn/interrupt).
                    if (args.Contains("compact-reject"))
                    {
                        Fail("compaction rejected");
                        break;
                    }
                    if (args.Contains("compact-no-start")) break;
                    if (!args.Contains("compact-no-ack") && !args.Contains("compact-late-ack"))
                        Reply(new JsonObject());
                    activeTurn = $"compact-{threads}";
                    Notify("turn/started", new JsonObject
                    {
                        ["threadId"] = threadId, ["turn"] = new JsonObject { ["id"] = activeTurn, ["status"] = "inProgress" }
                    });
                    Item("item/started", new JsonObject { ["type"] = "contextCompaction", ["id"] = "cc-1" });
                    if (args.Contains("compact-hang")) break;
                    Notify("thread/tokenUsage/updated", new JsonObject
                    {
                        ["threadId"] = threadId, ["turnId"] = activeTurn,
                        ["tokenUsage"] = new JsonObject { ["last"] = new JsonObject { ["inputTokens"] = 0 } }
                    });
                    if (args.Contains("compact-fail"))
                    {
                        Complete("failed", "compaction failed upstream");
                        break;
                    }
                    Item("item/completed", new JsonObject { ["type"] = "contextCompaction", ["id"] = "cc-1" });
                    Complete("completed");
                    if (args.Contains("compact-late-ack"))
                    {
                        Thread.Sleep(500);
                        Reply(new JsonObject());
                    }
                    break;
                case "thread/unsubscribe":
                    unsubscribed.Add((string?)parameters!["threadId"] ?? "?");
                    Reply(new JsonObject { ["status"] = "unsubscribed" });
                    break;
                case "account/read":
                    // Shaped like codex-cli 0.159.3. Scenarios: no-auth, api-key; ids and e-mail are fake.
                    Reply(new JsonObject
                    {
                        ["account"] = args.Contains("no-auth") ? null : args.Contains("api-key")
                            ? new JsonObject { ["type"] = "apiKey" }
                            : new JsonObject { ["type"] = "chatgpt", ["email"] = "fake@example.invalid", ["planType"] = "plus" },
                        ["requiresOpenaiAuth"] = true
                    });
                    break;
                case "account/rateLimits/read":
                    // Scenarios: old-cli, no-auth, upstream-error, hang-limits, multi-bucket, short-windows, single-view,
                    // percent-out-of-range.
                    if (args.Contains("hang-limits")) break;
                    if (args.Contains("old-cli"))
                    {
                        Fail("Invalid request: unknown variant `account/rateLimits/read`, expected one of `initialize`");
                        break;
                    }
                    if (args.Contains("no-auth"))
                    {
                        Fail("codex account authentication required to read rate limits");
                        break;
                    }
                    if (args.Contains("upstream-error"))
                    {
                        Fail("failed to fetch codex rate limits: 503 Service Unavailable");
                        break;
                    }

                    JsonObject Window(int used, int minutes, long resetsAt) => new()
                    {
                        ["usedPercent"] = used, ["windowDurationMins"] = minutes, ["resetsAt"] = resetsAt
                    };
                    JsonObject Bucket(string id, string? name, JsonObject? primary, JsonObject? secondary) => new()
                    {
                        ["limitId"] = id, ["limitName"] = name, ["primary"] = primary, ["secondary"] = secondary,
                        ["credits"] = new JsonObject { ["hasCredits"] = false, ["unlimited"] = false, ["balance"] = "0" },
                        ["planType"] = "plus", ["rateLimitReachedType"] = null
                    };
                    var codex = args.Contains("short-windows")
                        ? Bucket("codex", null, Window(25, 15, 4102444800), Window(42, 60, 4102444800))
                        : Bucket("codex", null, Window(37, 300, 4102444800),
                            Window(args.Contains("percent-out-of-range") ? 140 : 62, 10080, 4103049600));
                    var buckets = new JsonObject { ["codex"] = codex };
                    if (args.Contains("multi-bucket"))
                        buckets["codex_other"] = Bucket("codex_other", "Codex Other", Window(42, 60, 4102444800), null);
                    Reply(new JsonObject
                    {
                        ["rateLimits"] = codex.DeepClone(),
                        ["rateLimitsByLimitId"] = args.Contains("single-view") ? null : buckets,
                        ["rateLimitResetCredits"] = new JsonObject { ["availableCount"] = 1, ["credits"] = null },
                        ["accountId"] = "fake-account"
                    });
                    break;
                case "model/list":
                    // Shaped like codex-cli 0.159.3; the hidden model is never offered to the user.
                    JsonObject Model(string name, bool isDefault = false, bool hidden = false) => new()
                    {
                        ["id"] = name, ["model"] = name, ["displayName"] = name.ToUpperInvariant(), ["hidden"] = hidden,
                        ["isDefault"] = isDefault,
                        ["supportedReasoningEfforts"] = new JsonArray(
                            new JsonObject { ["reasoningEffort"] = "low" }, new JsonObject { ["reasoningEffort"] = "high" })
                    };

                    Reply((string?)parameters?["cursor"] == "page-2"
                        ? new JsonObject { ["data"] = new JsonArray(Model("fake-mini")), ["nextCursor"] = null }
                        : new JsonObject
                        {
                            ["data"] = new JsonArray(Model("fake-model", isDefault: true), Model("fake-hidden", hidden: true)),
                            ["nextCursor"] = "page-2"
                        });
                    break;
                case "turn/start":
                    if ((string?)parameters!["threadId"] != threadId)
                    {
                        Fail("unknown thread");
                        break;
                    }

                    if (activeTurn is not null)
                    {
                        // Like 0.157.1: absorbed by the active turn instead of starting a new one.
                        Reply(new JsonObject { ["turn"] = new JsonObject { ["id"] = activeTurn, ["status"] = "inProgress" } });
                        break;
                    }

                    if (parameters["sandboxPolicy"] is JsonObject newSandbox)
                    {
                        if (args.Contains("reject-mode"))
                        {
                            Fail("mode rejected");
                            break;
                        }
                        newSandbox["networkAccess"] ??= false;
                        threadParams!["approvalPolicy"] = parameters["approvalPolicy"]!.DeepClone();
                        threadParams["approvalsReviewer"] = parameters["approvalsReviewer"]!.DeepClone();
                        threadParams["sandbox"] = (string?)newSandbox["type"] == "readOnly" ? "read-only" : "workspace-write";
                        if (!args.Contains("missing-mode-confirmation"))
                            Notify("thread/settings/updated", new JsonObject
                            {
                                ["threadId"] = threadId,
                                ["threadSettings"] = new JsonObject
                                {
                                    ["cwd"] = threadParams["cwd"]!.DeepClone(),
                                    ["model"] = (string?)threadParams["model"] ?? "fake-model",
                                    ["effort"] = parameters["effort"]?.DeepClone(),
                                    ["approvalPolicy"] = threadParams["approvalPolicy"]!.DeepClone(),
                                    ["approvalsReviewer"] = args.Contains("wrong-mode-reviewer") ? "unexpected" : threadParams["approvalsReviewer"]!.DeepClone(),
                                    ["sandboxPolicy"] = args.Contains("wrong-mode-sandbox")
                                        ? new JsonObject { ["type"] = "dangerFullAccess", ["networkAccess"] = true }
                                        : newSandbox.DeepClone(),
                                    ["collaborationMode"] = parameters["collaborationMode"]!.DeepClone()
                                }
                            });
                    }

                    turns++;
                    turnParams = parameters;
                    activeTurn = $"turn-{turns}";
                    Reply(new JsonObject
                    {
                        ["turn"] = new JsonObject { ["id"] = activeTurn, ["items"] = new JsonArray(), ["status"] = "inProgress" }
                    });
                    Notify("turn/started", new JsonObject
                    {
                        ["threadId"] = threadId, ["turn"] = new JsonObject { ["id"] = activeTurn, ["status"] = "inProgress" }
                    });
                    var text = (string?)parameters["input"]![0]!["text"];
                    if (text?.StartsWith("generate-image ", StringComparison.Ordinal) == true)
                    {
                        // Like 0.159.3: the generated image is an item whose savedPath is where the CLI wrote it (#97).
                        Item("item/completed", new JsonObject
                        {
                            ["type"] = "imageGeneration", ["id"] = $"ig-{turns}", ["status"] = "completed",
                            ["savedPath"] = text["generate-image ".Length..]
                        });
                        Message($"Gerei a imagem. O arquivo está em {text["generate-image ".Length..]}");
                        Complete("completed");
                        break;
                    }

                    switch (text)
                    {
                        case "thread":
                            // A late item of the previous thread must not reach the session after a clear (#120).
                            if (previousThread is not null)
                                Notify("item/completed", new JsonObject
                                {
                                    ["threadId"] = previousThread, ["turnId"] = "old-turn",
                                    ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = "stale", ["text"] = "stale" }
                                });
                            Message($"thread:{threadId};turn:{turns};model:{(string?)threadParams!["model"] ?? "-"};" +
                                    $"sandbox:{(string?)threadParams["sandbox"]};unsubscribed:{string.Join(",", unsubscribed)}");
                            Complete("completed");
                            break;
                        case "pong":
                            foreach (var part in new[] { "po", "ng" })
                            {
                                Notify("item/agentMessage/delta", new JsonObject
                                {
                                    ["threadId"] = threadId, ["turnId"] = activeTurn, ["itemId"] = $"msg-{turns}",
                                    ["delta"] = part
                                });
                            }

                            Item("item/completed", new JsonObject
                            {
                                ["type"] = "agentMessage", ["id"] = $"msg-{turns}", ["text"] = $"pong {turns}"
                            });
                            Complete("completed");
                            break;
                        case "describe-input":
                            // Reports every input item in order: what a turn with attachments sent (#95).
                            Message("input:" + Describe(parameters["input"]!.AsArray()));
                            Complete("completed");
                            break;
                        case "config":
                            var mode = turnParams["collaborationMode"] as JsonObject;
                            Message($"{threadParams!["approvalPolicy"]}/{threadParams["sandbox"]}/" +
                                    $"{(string?)mode?["mode"] ?? "default"}/{(string?)mode?["settings"]?["model"]}/" +
                                    $"{threadParams["ephemeral"]}/{threadParams["approvalsReviewer"]}");
                            Complete("completed");
                            break;
                        case "command":
                            Item("item/started", new JsonObject
                            {
                                ["type"] = "commandExecution", ["id"] = "cmd-1", ["command"] = "dotnet test",
                                ["status"] = "inProgress"
                            });
                            ServerRequest("command", "item/commandExecution/requestApproval", new JsonObject
                            {
                                ["itemId"] = "cmd-1", ["command"] = "dotnet test", ["reason"] = "rodar os testes"
                            });
                            break;
                        case "edit":
                            Item("item/started", new JsonObject
                            {
                                ["type"] = "fileChange", ["id"] = "fc-1", ["status"] = "inProgress",
                                ["changes"] = new JsonArray(new JsonObject
                                {
                                    ["path"] = "a.txt", ["kind"] = new JsonObject { ["type"] = "add" }, ["diff"] = "+hi"
                                })
                            });
                            ServerRequest("edit", "item/fileChange/requestApproval", new JsonObject
                            {
                                ["itemId"] = "fc-1", ["reason"] = "criar a.txt"
                            });
                            break;
                        case "ask":
                            ServerRequest("ask", "item/tool/requestUserInput", new JsonObject
                            {
                                ["itemId"] = "ask-1",
                                ["isBlocking"] = true,
                                ["questions"] = new JsonArray(new JsonObject
                                {
                                    ["id"] = "codename", ["header"] = "Projeto", ["question"] = "Qual o codinome?",
                                    ["options"] = new JsonArray(
                                        new JsonObject { ["label"] = "alpha", ["description"] = "A" },
                                        new JsonObject { ["label"] = "beta", ["description"] = "B" })
                                })
                            });
                            break;
                        case "elicit":
                            ServerRequest("elicit", "mcpServer/elicitation/request", new JsonObject());
                            break;
                        case "slow":
                            // Output of a turn that only ends when interrupted or steered.
                            Message("working");
                            break;
                        case "fail":
                            Complete("failed", "boom");
                            break;
                        case "warn":
                            Notify("warning", new JsonObject { ["message"] = "cuidado" });
                            Notify("error", new JsonObject
                            {
                                ["threadId"] = threadId, ["turnId"] = activeTurn, ["willRetry"] = true,
                                ["error"] = new JsonObject { ["message"] = "tentando de novo" }
                            });
                            Complete("completed");
                            break;
                        case "garbage":
                            Console.WriteLine("this is not json");
                            break;
                        case "crash":
                            return 5;
                        case "effort":
                            var requestedEffort = (string?)turnParams["effort"] ?? "default";
                            var planEffort = (string?)turnParams["collaborationMode"]?["settings"]?["reasoning_effort"];
                            Message("effort:" + requestedEffort + "/" + (planEffort ?? "default"));
                            Complete("completed");
                            break;
                        case "model":
                            // Reports the model thread/start received.
                            Message("model:" + ((string?)threadParams!["model"] ?? "default"));
                            Complete("completed");
                            break;
                    }

                    break;
                case "turn/steer":
                    if (activeTurn is null || (string?)parameters!["expectedTurnId"] != activeTurn)
                    {
                        Fail("no active turn to steer");
                        break;
                    }

                    Reply(new JsonObject { ["turnId"] = activeTurn });
                    var steer = parameters["input"]!.AsArray();
                    Message("steered:" + (string?)steer[0]!["text"] +
                            (steer.Count == 1 ? string.Empty : "|" + Describe(new JsonArray(steer.Skip(1)
                                .Select(item => item!.DeepClone()).ToArray()))));
                    Complete("completed");
                    break;
                case "turn/interrupt":
                    if (activeTurn is null || (string?)parameters!["turnId"] != activeTurn)
                    {
                        Fail("no active turn to interrupt");
                        break;
                    }

                    Reply(new JsonObject());
                    Complete("interrupted");
                    break;
                default:
                    Fail($"unknown method {method}");
                    break;
            }
        }

        return 0;
    }

    private static string Describe(JsonArray input) => string.Join("|", input.Select(item =>
        (string?)item!["type"] switch
        {
            "text" => "text:" + (string?)item["text"],
            "localImage" => "localImage:" + (string?)item["path"],
            var other => other ?? "?"
        }));
}
