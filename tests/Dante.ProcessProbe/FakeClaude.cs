using System.Text.Json.Nodes;

namespace Dante.ProcessProbe;

// Simulated `claude --print` stream-json session, shaped like the messages captured from Claude Code 2.1.284.
// Each user message picks a scenario by its text: pong, write, ask, slow, fail, garbage, crash or model.
internal static class FakeClaude
{
    public static int Run(string[] args)
    {
        var sessionId = ValueAfter(args, "--session-id") ?? "no-session-id";
        var turns = 0;
        string? waitingFor = null;
        var turnActive = false;

        void Send(JsonObject message)
        {
            message["session_id"] = sessionId;
            Console.WriteLine(message.ToJsonString());
        }

        void Assistant(string text) => Send(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["id"] = $"msg_{turns}",
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
            }
        });

        void Result(bool success, string text)
        {
            turnActive = false;
            waitingFor = null;
            Send(new JsonObject
            {
                ["type"] = "result",
                ["subtype"] = success ? "success" : "error_during_execution",
                ["is_error"] = !success,
                ["result"] = text
            });
        }

        void ControlRequest(string requestId, JsonObject request)
        {
            waitingFor = requestId;
            Send(new JsonObject { ["type"] = "control_request", ["request_id"] = requestId, ["request"] = request });
        }

        while (Console.In.ReadLine() is { } line)
        {
            var message = JsonNode.Parse(line)!.AsObject();
            switch ((string?)message["type"])
            {
                case "control_request":
                    var request = message["request"]!.AsObject();
                    if ((string?)request["subtype"] == "initialize" && args.Contains("reject-init"))
                    {
                        Send(ControlResponse((string)message["request_id"]!, "error", "initialize rejected"));
                        break;
                    }

                    Send(ControlResponse((string)message["request_id"]!, "success",
                        payload: (string?)request["subtype"] == "initialize" ? Initialize() : null));
                    if ((string?)request["subtype"] == "interrupt" && turnActive)
                    {
                        Result(false, "");
                    }

                    break;
                case "control_response":
                    var response = message["response"]!.AsObject();
                    if ((string?)response["request_id"] != waitingFor)
                    {
                        break;
                    }

                    var decision = response["response"]!.AsObject();
                    if (waitingFor == "perm-write")
                    {
                        var allowed = (string?)decision["behavior"] == "allow";
                        Send(new JsonObject
                        {
                            ["type"] = "user",
                            ["message"] = new JsonObject
                            {
                                ["role"] = "user",
                                ["content"] = new JsonArray(new JsonObject
                                {
                                    ["type"] = "tool_result",
                                    ["tool_use_id"] = "toolu_write",
                                    ["content"] = allowed ? "File created successfully" : (string?)decision["message"],
                                    ["is_error"] = !allowed
                                })
                            }
                        });
                        // Reports the destination of every permission update Claude would apply ("once" if none).
                        var scope = decision["updatedPermissions"] is JsonArray { Count: > 0 } updates
                            ? string.Join(",", updates.Select(update => (string?)update!["destination"]))
                            : "once";
                        Assistant(allowed ? $"allow:{scope}" : $"deny:{decision["message"]}");
                    }
                    else
                    {
                        Assistant("answers:" + decision["updatedInput"]!["answers"]!.ToJsonString());
                    }

                    Result(true, "done");
                    break;
                case "user":
                    turns++;
                    turnActive = true;
                    Send(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["cwd"] = Environment.CurrentDirectory });
                    switch ((string?)message["message"]!["content"])
                    {
                        case "pong":
                            Send(StreamEvent(new JsonObject
                            {
                                ["type"] = "message_start",
                                ["message"] = new JsonObject { ["id"] = $"msg_{turns}" }
                            }));
                            foreach (var part in new[] { "po", "ng" })
                            {
                                Send(StreamEvent(new JsonObject
                                {
                                    ["type"] = "content_block_delta",
                                    ["index"] = 0,
                                    ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = part }
                                }));
                            }

                            Assistant($"pong {turns}");
                            Result(true, $"pong {turns}");
                            break;
                        case "write" or "write-persistent" or "write-mixed":
                            var input = new JsonObject { ["file_path"] = "notes.txt", ["content"] = "hi" };
                            Send(new JsonObject
                            {
                                ["type"] = "assistant",
                                ["message"] = new JsonObject
                                {
                                    ["id"] = $"msg_{turns}",
                                    ["content"] = new JsonArray(new JsonObject
                                    {
                                        ["type"] = "tool_use", ["id"] = "toolu_write", ["name"] = "Write",
                                        ["input"] = input.DeepClone()
                                    })
                                }
                            });
                            ControlRequest("perm-write", new JsonObject
                            {
                                ["subtype"] = "can_use_tool",
                                ["tool_name"] = "Write",
                                ["input"] = input,
                                ["permission_suggestions"] = WriteSuggestions((string)message["message"]!["content"]!),
                                ["tool_use_id"] = "toolu_write"
                            });
                            break;
                        case "ask":
                            ControlRequest("ask-color", new JsonObject
                            {
                                ["subtype"] = "can_use_tool",
                                ["tool_name"] = "AskUserQuestion",
                                ["input"] = new JsonObject
                                {
                                    ["questions"] = new JsonArray(new JsonObject
                                    {
                                        ["question"] = "Which color?",
                                        ["header"] = "Color",
                                        ["options"] = new JsonArray(
                                            new JsonObject { ["label"] = "red", ["description"] = "Red" },
                                            new JsonObject { ["label"] = "blue", ["description"] = "Blue" }),
                                        ["multiSelect"] = false
                                    })
                                }
                            });
                            break;
                        case "slow":
                            // Output of a turn that only ends when interrupted.
                            Assistant("working");
                            break;
                        case "fail":
                            Result(false, "boom");
                            break;
                        case "garbage":
                            Console.WriteLine("this is not json");
                            break;
                        case "crash":
                            return 5;
                        case "effort":
                            Assistant("effort:" + (ValueAfter(args, "--effort") ?? "default"));
                            Result(true, "done");
                            break;
                        case "model":
                            // Reports the --model the session was started with.
                            Assistant("model:" + (ValueAfter(args, "--model") ?? "default"));
                            Result(true, "done");
                            break;
                    }

                    break;
            }
        }

        return 0;
    }

    // "write" offers only a session suggestion, "write-persistent" only persistent ones, "write-mixed" both.
    private static JsonArray WriteSuggestions(string prompt)
    {
        var session = new JsonObject { ["type"] = "setMode", ["mode"] = "acceptEdits", ["destination"] = "session" };
        JsonObject Rule(string destination) => new()
        {
            ["type"] = "addRules",
            ["rules"] = new JsonArray(new JsonObject { ["toolName"] = "Write" }),
            ["behavior"] = "allow",
            ["destination"] = destination
        };

        return prompt switch
        {
            "write-persistent" => [Rule("userSettings"), Rule("projectSettings"), Rule("localSettings")],
            "write-mixed" => [Rule("userSettings"), session, Rule("projectSettings"), Rule("session"),
                Rule("localSettings")],
            _ => [session]
        };
    }

    // The models part of the initialize response, shaped like Claude Code 2.1.286: "default" is the CLI default itself
    // and names the model it resolves to.
    private static JsonObject Initialize()
    {
        JsonObject Model(string value, string resolved, string displayName, bool effort = true)
        {
            var model = new JsonObject
            {
                ["value"] = value, ["resolvedModel"] = resolved, ["displayName"] = displayName
            };
            if (effort)
            {
                model["supportsEffort"] = true;
                model["supportedEffortLevels"] = new JsonArray("low", "medium", "high");
            }

            return model;
        }

        return new JsonObject
        {
            ["models"] = new JsonArray(
                Model("default", "claude-opus-test", "Default (recommended)"),
                Model("opus", "claude-opus-test", "Opus Test"),
                Model("sonnet", "claude-sonnet-test", "Sonnet Test"),
                Model("claude-legacy-test", "claude-legacy-test", "Legacy Test", effort: false))
        };
    }

    private static JsonObject ControlResponse(string requestId, string subtype, string? error = null,
        JsonObject? payload = null)
    {
        var response = new JsonObject { ["subtype"] = subtype, ["request_id"] = requestId };
        if (error is null)
        {
            response["response"] = payload ?? new JsonObject();
        }
        else
        {
            response["error"] = error;
        }

        return new JsonObject { ["type"] = "control_response", ["response"] = response };
    }

    private static JsonObject StreamEvent(JsonObject streamEvent) =>
        new() { ["type"] = "stream_event", ["event"] = streamEvent };

    private static string? ValueAfter(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
