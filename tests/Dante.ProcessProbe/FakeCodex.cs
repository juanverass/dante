using System.Text.Json.Nodes;

namespace Dante.ProcessProbe;

// Simulated `codex app-server --listen stdio://` (JSON-RPC in JSONL), shaped like codex-cli 0.157.1: responses have no
// "jsonrpc" field, like the real server. Each turn picks a scenario by its text: pong, config, command, edit, ask,
// slow, fail, warn, elicit, garbage, crash or model. model/list answers in two pages.
internal static class FakeCodex
{
    private const string ThreadId = "thread-1";

    public static int Run(string[] args)
    {
        var turns = 0;
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
            ["threadId"] = ThreadId, ["turnId"] = activeTurn, ["item"] = item
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
            Notify("turn/completed", new JsonObject { ["threadId"] = ThreadId, ["turn"] = turn });
        }

        void ServerRequest(string scenario, string method, JsonObject parameters)
        {
            waitingScenario = scenario;
            waitingFor = ++nextServerRequest;
            parameters["threadId"] = ThreadId;
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

                    threadParams = parameters;
                    Reply(new JsonObject
                    {
                        ["thread"] = new JsonObject { ["id"] = ThreadId, ["cwd"] = (string?)parameters!["cwd"] },
                        ["model"] = (string?)parameters["model"] ?? "fake-model"
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
                    if ((string?)parameters!["threadId"] != ThreadId)
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

                    turns++;
                    turnParams = parameters;
                    activeTurn = $"turn-{turns}";
                    Reply(new JsonObject
                    {
                        ["turn"] = new JsonObject { ["id"] = activeTurn, ["items"] = new JsonArray(), ["status"] = "inProgress" }
                    });
                    Notify("turn/started", new JsonObject
                    {
                        ["threadId"] = ThreadId, ["turn"] = new JsonObject { ["id"] = activeTurn, ["status"] = "inProgress" }
                    });
                    var text = (string?)parameters["input"]![0]!["text"];
                    switch (text)
                    {
                        case "pong":
                            foreach (var part in new[] { "po", "ng" })
                            {
                                Notify("item/agentMessage/delta", new JsonObject
                                {
                                    ["threadId"] = ThreadId, ["turnId"] = activeTurn, ["itemId"] = $"msg-{turns}",
                                    ["delta"] = part
                                });
                            }

                            Item("item/completed", new JsonObject
                            {
                                ["type"] = "agentMessage", ["id"] = $"msg-{turns}", ["text"] = $"pong {turns}"
                            });
                            Complete("completed");
                            break;
                        case "config":
                            var mode = turnParams["collaborationMode"] as JsonObject;
                            Message($"{threadParams!["approvalPolicy"]}/{threadParams["sandbox"]}/" +
                                    $"{(string?)mode?["mode"] ?? "default"}/{(string?)mode?["settings"]?["model"]}/" +
                                    $"{threadParams["ephemeral"]}");
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
                                ["threadId"] = ThreadId, ["turnId"] = activeTurn, ["willRetry"] = true,
                                ["error"] = new JsonObject { ["message"] = "tentando de novo" }
                            });
                            Complete("completed");
                            break;
                        case "garbage":
                            Console.WriteLine("this is not json");
                            break;
                        case "crash":
                            return 5;
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
                    Message("steered:" + (string?)parameters["input"]![0]!["text"]);
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
}
