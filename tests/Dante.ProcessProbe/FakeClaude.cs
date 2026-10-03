using System.Text.Json.Nodes;

namespace Dante.ProcessProbe;

// Simulated `claude --print` stream-json session, shaped like the messages captured from Claude Code 2.1.284.
// Each user message picks a scenario by its text: pong, write, ask, slow, fail, garbage, crash or model. get_usage
// answers the quota query (#117) without a turn.
internal static class FakeClaude
{
    public static int Run(string[] args)
    {
        var sessionId = ValueAfter(args, "--session-id") ?? "no-session-id";
        var mode = ValueAfter(args, "--permission-mode") ?? "manual";
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
                    if ((string?)request["subtype"] == "set_permission_mode")
                    {
                        var requested = (string?)request["mode"];
                        if (args.Contains("reject-mode"))
                            Send(ControlResponse((string)message["request_id"]!, "error", "mode rejected"));
                        else
                        {
                            mode = requested == "manual" ? "default" : requested!;
                            Send(ControlResponse((string)message["request_id"]!, "success", payload:
                                args.Contains("missing-mode") ? new JsonObject() : new JsonObject { ["mode"] = mode }));
                        }
                        break;
                    }
                    if ((string?)request["subtype"] == "get_usage")
                    {
                        // Scenarios: old-cli, hang-usage, api-key, no-auth, usage-down, partial-usage, model-limits.
                        if (args.Contains("hang-usage")) break;
                        Send(args.Contains("old-cli")
                            ? ControlResponse((string)message["request_id"]!, "error",
                                "Unsupported control request subtype: get_usage")
                            : request["skip_behaviors"]?.GetValue<bool>() != true
                                ? ControlResponse((string)message["request_id"]!, "error", "behaviors scan not expected")
                                : ControlResponse((string)message["request_id"]!, "success", payload: Usage(args)));
                        break;
                    }
                    if ((string?)request["subtype"] == "initialize" && args.Contains("reject-init"))
                    {
                        Send(ControlResponse((string)message["request_id"]!, "error", "initialize rejected"));
                        break;
                    }

                    Send(ControlResponse((string)message["request_id"]!, "success",
                        payload: (string?)request["subtype"] == "initialize" ? Initialize(args) : null));
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
                    if (message["message"]!["content"] is JsonArray blocks)
                    {
                        // Reports every content block in order: what a turn with attachments sent (#95).
                        Assistant("content:" + string.Join("|", blocks.Select(Describe)));
                        Result(true, "done");
                        break;
                    }

                    switch ((string?)message["message"]!["content"])
                    {
                        case "mode":
                            Assistant($"mode:{mode}");
                            Result(true, "done");
                            break;
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

    // Images are reported by media type and decoded size, never by content.
    private static string Describe(JsonNode? block) => (string?)block!["type"] switch
    {
        "text" => "text:" + (string?)block["text"],
        "image" => $"image:{(string?)block["source"]!["media_type"]}:" +
                   Convert.FromBase64String((string)block["source"]!["data"]!).Length,
        var other => other ?? "?"
    };

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
    private static JsonObject Initialize(string[] args)
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
            // Shaped like Claude Code 2.1.287 for a claude.ai login, an API key and no login; e-mail is fake.
            ["account"] = args.Contains("api-key")
                ? new JsonObject { ["tokenSource"] = "claude.ai", ["apiKeySource"] = "ANTHROPIC_API_KEY", ["apiProvider"] = "firstParty" }
                : args.Contains("no-auth")
                    ? new JsonObject { ["tokenSource"] = "none", ["apiProvider"] = "firstParty" }
                    : new JsonObject
                    {
                        ["email"] = "fake@example.invalid", ["subscriptionType"] = "Claude Pro", ["apiProvider"] = "firstParty"
                    },
            ["models"] = new JsonArray(
                Model("default", "claude-opus-test", "Default (recommended)"),
                Model("opus", "claude-opus-test", "Opus Test"),
                Model("sonnet", "claude-sonnet-test", "Sonnet Test"),
                Model("claude-legacy-test", "claude-legacy-test", "Legacy Test", effort: false))
        };
    }

    // The quota part of the get_usage answer (#117), shaped like Claude Code 2.1.287: utilization is 0–100 and resets_at
    // is ISO 8601. limits and the code-named keys are outside the documented schema, like the real answer.
    private static JsonObject Usage(string[] args)
    {
        JsonObject Window(double? utilization, string? resetsAt) => new()
        {
            ["utilization"] = utilization, ["resets_at"] = resetsAt, ["limit_dollars"] = null
        };

        if (args.Contains("api-key") || args.Contains("no-auth"))
            return new JsonObject { ["subscription_type"] = null, ["rate_limits_available"] = false, ["rate_limits"] = null };
        if (args.Contains("usage-down"))
            return new JsonObject { ["subscription_type"] = "pro", ["rate_limits_available"] = true, ["rate_limits"] = null };
        var limits = args.Contains("partial-usage")
            ? new JsonObject { ["five_hour"] = Window(41, null), ["seven_day"] = null }
            : new JsonObject
            {
                ["five_hour"] = Window(41, "2100-01-01T05:19:59.531475+00:00"),
                ["seven_day"] = Window(58, "2100-01-03T04:59:59.531502+00:00"),
                ["seven_day_opus"] = args.Contains("model-limits") ? Window(73.5, "2100-01-03T04:59:59+00:00") : null,
                ["seven_day_sonnet"] = null,
                ["seven_day_oauth_apps"] = null,
                ["iguana_necktie"] = Window(0, "2100-02-01T07:59:00+00:00"),
                ["limits"] = new JsonArray(new JsonObject { ["kind"] = "session", ["group"] = "session", ["percent"] = 41 })
            };
        if (args.Contains("model-limits"))
            limits["model_scoped"] = new JsonArray(new JsonObject
            {
                ["display_name"] = "Fable", ["utilization"] = 12, ["resets_at"] = "2100-01-03T04:59:59+00:00"
            });
        return new JsonObject
        {
            ["session"] = new JsonObject { ["total_cost_usd"] = 0 },
            ["subscription_type"] = "pro",
            ["rate_limits_available"] = true,
            ["rate_limits"] = limits,
            ["behaviors"] = null
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
