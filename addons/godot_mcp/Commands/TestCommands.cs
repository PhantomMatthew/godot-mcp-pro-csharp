using System.Linq;
using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Test automation framework tools: editor-side orchestration plus runtime
/// assertions via file-based IPC with the running game.
/// Faithful port of addons/godot_mcp/commands/test_commands.gd.
///
/// This module is instantiated once and lives as a persistent child node of
/// the command router, so <see cref="_testResults"/> accumulates assertion
/// verdicts across calls exactly like the GDScript module-level state.
/// </summary>
[Tool]
public partial class TestCommands : BaseCommand
{
    // ── Internal test result accumulator ──────────────────────────────────────

    private readonly List<Dictionary> _testResults = new();

    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "run_test_scenario",
            Description = "Execute a test scenario: optionally play a scene, then run a sequence of steps " +
                "(input simulation, waits, assertions, screenshots) and return pass/fail results. Each step " +
                "is a dictionary with a 'type' of input|wait|assert|screenshot plus type-specific parameters.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["steps"] = Schema.Arr(
                    "Ordered array of step dictionaries. input: {type:'input', action|keycode, pressed, " +
                    "auto_release, strength|shift/ctrl/alt, frame_delay}. wait: {type:'wait', seconds} or " +
                    "{type:'wait', node_path, timeout, poll_frames}. assert: {type:'assert', text, partial} " +
                    "or {type:'assert', node_path, property, expected, operator}. screenshot: " +
                    "{type:'screenshot', half_resolution}.",
                    Schema.Obj("Test scenario step definition.")),
                ["scene_path"] = Schema.Str(
                    "Scene to play before running steps: 'main' (main scene), 'current' (currently edited " +
                    "scene), a res:// path to a specific scene, or omitted to use whatever is already playing.",
                    ""),
            }, "steps"),
            Category = "test",
            Handler = RunTestScenario,
        },
        new()
        {
            Name = "assert_node_state",
            Description = "Assert a node's property equals (or otherwise compares to) an expected value in " +
                "the running game. Supports eq/neq/gt/lt/gte/lte/contains/type_is operators. Returns pass/fail " +
                "plus the actual value read from the game.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the node in the running game's scene tree."),
                ["property"] = Schema.Str("Name of the property to read on the node."),
                ["expected"] = Schema.Any("Expected value to compare the property against."),
                ["operator"] = Schema.Enum(
                    "Comparison operator to apply between the actual and expected value.",
                    "eq", "neq", "gt", "lt", "gte", "lte", "contains", "type_is"),
            }, "node_path", "property", "expected"),
            Category = "test",
            Handler = AssertNodeState,
        },
        new()
        {
            Name = "assert_screen_text",
            Description = "Assert that specific text is visible on screen in the running game, by scanning " +
                "all visible UI text via find_ui_elements.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["text"] = Schema.Str("Text expected to be visible on screen."),
                ["partial"] = Schema.Bool("Match if the expected text is a substring of visible text.", true),
                ["case_sensitive"] = Schema.Bool("Whether the text comparison is case-sensitive.", true),
            }, "text"),
            Category = "test",
            Handler = AssertScreenText,
        },
        new()
        {
            Name = "run_stress_test",
            Description = "Run rapid random inputs against the running game for N seconds (max 60) and check " +
                "for crashes or new runtime errors. Returns event count, timing, and the error-count delta.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["duration"] = Schema.Num("Duration of the stress test in seconds (0 < duration <= 60).", 5.0),
                ["actions"] = Schema.Arr(
                    "Additional custom input-action names to mix into the random input pool, alongside the " +
                    "default ui_up/ui_down/ui_left/ui_right/ui_accept/ui_cancel actions.",
                    Schema.Str("Input action name.")),
            }),
            Category = "test",
            Handler = RunStressTest,
        },
        new()
        {
            Name = "get_test_report",
            Description = "Collect and format results from accumulated assert_node_state/assert_screen_text/" +
                "run_test_scenario assertions into a test report with pass/fail counts and details.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["clear"] = Schema.Bool("Clear the accumulated results after producing the report.", true),
            }),
            Category = "test",
            Handler = p => Task.FromResult(GetTestReport(p)),
        },
    };

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task<Dictionary> RunTestScenario(Dictionary @params)
    {
        if (!@params.TryGetValue("steps", out var stepsVariant) || stepsVariant.VariantType != Variant.Type.Array)
            return ErrorInvalidParams("Missing required parameter: steps (Array)");

        var steps = stepsVariant.AsGodotArray();
        if (steps.Count == 0)
            return ErrorInvalidParams("Steps array is empty");

        var scenePath = OptionalString(@params, "scene_path");
        var ei = GetEditor();

        // Play scene if requested
        if (!string.IsNullOrEmpty(scenePath))
        {
            if (ei.IsPlayingScene())
            {
                ei.StopPlayingScene();
                await DelaySeconds(0.5);
            }

            if (scenePath == "main")
                ei.PlayMainScene();
            else if (scenePath == "current")
                ei.PlayCurrentScene();
            else
            {
                if (!Godot.FileAccess.FileExists(scenePath))
                    return ErrorNotFound($"Scene file '{scenePath}'");
                ei.PlayCustomScene(scenePath);
            }

            // Wait for game to start
            await DelaySeconds(1.0);
        }

        // Verify game is running
        if (!ei.IsPlayingScene())
            return Error(CodeGeneric, "No scene is currently playing",
                new Dictionary { ["suggestion"] = "Provide scene_path or use play_scene first" });

        var results = new Godot.Collections.Array();
        var passCount = 0;
        var failCount = 0;
        var errorCount = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].VariantType != Variant.Type.Dictionary)
            {
                results.Add(new Dictionary { ["step"] = i, ["error"] = "Missing 'type' field" });
                errorCount++;
                continue;
            }

            var step = steps[i].AsGodotDictionary();
            if (!step.ContainsKey("type"))
            {
                results.Add(new Dictionary { ["step"] = i, ["error"] = "Missing 'type' field" });
                errorCount++;
                continue;
            }

            var stepType = step["type"].AsString();
            var stepResult = new Dictionary { ["step"] = i, ["type"] = stepType };

            switch (stepType)
            {
                case "input":
                {
                    var inputResult = ExecuteInputStep(step);
                    MergeInto(stepResult, inputResult);
                    break;
                }
                case "wait":
                {
                    var waitResult = await ExecuteWaitStep(step);
                    MergeInto(stepResult, waitResult);
                    break;
                }
                case "assert":
                {
                    var assertResult = await ExecuteAssertStep(step);
                    MergeInto(stepResult, assertResult);
                    var passed = assertResult.TryGetValue("passed", out var passedVariant) && passedVariant.AsBool();
                    if (passed)
                        passCount++;
                    else
                        failCount++;
                    // Only assertion steps carry a verdict — store just these for
                    // get_test_report (input/wait/screenshot steps have no "passed").
                    _testResults.Add(stepResult.Duplicate(true));
                    break;
                }
                case "screenshot":
                {
                    var screenshotResult = await SendGameCommand("capture_frames", new Dictionary
                    {
                        ["count"] = 1,
                        ["frame_interval"] = 1,
                        ["half_resolution"] = OptionalBool(step, "half_resolution", true),
                    }, 5.0);
                    if (screenshotResult.ContainsKey("result"))
                        stepResult["captured"] = true;
                    else
                    {
                        stepResult["captured"] = false;
                        stepResult["error"] = "Screenshot capture failed";
                        errorCount++;
                    }
                    break;
                }
                default:
                    stepResult["error"] = $"Unknown step type: {stepType}";
                    errorCount++;
                    break;
            }

            results.Add(stepResult);

            // Check if game crashed between steps
            if (!ei.IsPlayingScene())
            {
                results.Add(new Dictionary { ["step"] = i + 1, ["error"] = "Game stopped unexpectedly" });
                errorCount++;
                break;
            }
        }

        var summary = new Dictionary
        {
            ["total_steps"] = steps.Count,
            ["completed_steps"] = results.Count,
            ["assertions_passed"] = passCount,
            ["assertions_failed"] = failCount,
            ["errors"] = errorCount,
            ["all_passed"] = failCount == 0 && errorCount == 0,
            ["results"] = results,
        };

        return Success(summary);
    }

    private async Task<Dictionary> AssertNodeState(Dictionary @params)
    {
        var (nodePath, pathErr) = RequireString(@params, "node_path");
        if (pathErr != null)
            return pathErr;

        var (property, propErr) = RequireString(@params, "property");
        if (propErr != null)
            return propErr;

        if (!@params.ContainsKey("expected"))
            return ErrorInvalidParams("Missing required parameter: expected");

        var operatorName = OptionalString(@params, "operator", "eq");
        var validOperators = new[] { "eq", "neq", "gt", "lt", "gte", "lte", "contains", "type_is" };
        if (System.Array.IndexOf(validOperators, operatorName) < 0)
            return ErrorInvalidParams($"Invalid operator '{operatorName}'. Valid: {FormatStringArray(validOperators)}");

        var result = await SendGameCommand("assert_node_state", new Dictionary
        {
            ["node_path"] = nodePath,
            ["property"] = property,
            ["expected"] = @params["expected"],
            ["operator"] = operatorName,
        }, 5.0);

        if (result.ContainsKey("error"))
            return result;

        // The game reply is wrapped twice ({"result": {"result": {...}}}) — unwrap
        // defensively before storing/returning so "passed" sits at the top level.
        var payload = UnwrapGameResult(result);
        if (payload.ContainsKey("passed"))
            _testResults.Add(payload.Duplicate(true));
        return Success(payload);
    }

    private async Task<Dictionary> AssertScreenText(Dictionary @params)
    {
        var (expectedText, err) = RequireString(@params, "text");
        if (err != null)
            return err;

        var partial = OptionalBool(@params, "partial", true);
        var caseSensitive = OptionalBool(@params, "case_sensitive", true);

        // Use find_ui_elements to get all visible UI text
        var uiResult = await SendGameCommand("find_ui_elements", new Dictionary());
        if (uiResult.ContainsKey("error"))
            return uiResult;

        var elements = ExtractElements(uiResult);

        var found = false;
        var matchedElement = new Dictionary();
        var allTexts = new Godot.Collections.Array();

        foreach (var elementVariant in elements)
        {
            if (elementVariant.VariantType != Variant.Type.Dictionary)
                continue;
            var element = elementVariant.AsGodotDictionary();
            var elementText = element.TryGetValue("text", out var textVariant) ? textVariant.AsString() : "";
            if (string.IsNullOrEmpty(elementText))
                continue;
            allTexts.Add(elementText);

            var searchText = expectedText;
            var compareText = elementText;
            if (!caseSensitive)
            {
                searchText = searchText.ToLowerInvariant();
                compareText = compareText.ToLowerInvariant();
            }

            if (partial ? compareText.Contains(searchText) : compareText == searchText)
            {
                found = true;
                matchedElement = element;
                break;
            }
        }

        var assertion = new Dictionary
        {
            ["passed"] = found,
            ["expected_text"] = expectedText,
            ["partial"] = partial,
            ["case_sensitive"] = caseSensitive,
        };

        if (found)
        {
            assertion["matched_element"] = new Dictionary
            {
                ["text"] = matchedElement.TryGetValue("text", out var mText) ? mText : "",
                ["type"] = matchedElement.TryGetValue("type", out var mType) ? mType : "",
                ["path"] = matchedElement.TryGetValue("path", out var mPath) ? mPath : "",
            };
        }
        else
        {
            assertion["visible_texts"] = allTexts;
        }

        // Store for test report
        _testResults.Add(assertion.Duplicate(true));

        return Success(assertion);
    }

    private async Task<Dictionary> RunStressTest(Dictionary @params)
    {
        var duration = OptionalFloat(@params, "duration", 5.0);
        if (duration <= 0 || duration > 60)
            return ErrorInvalidParams("Duration must be between 0 and 60 seconds");

        var ei = GetEditor();
        if (!ei.IsPlayingScene())
            return Error(CodeGeneric, "No scene is currently playing",
                new Dictionary { ["suggestion"] = "Use play_scene first" });

        // Record initial error count from log
        var initialErrors = CountLogErrors();

        // Generate random input events
        var actions = new List<string> { "ui_up", "ui_down", "ui_left", "ui_right", "ui_accept", "ui_cancel" };
        var customActions = OptionalArray(@params, "actions");
        if (customActions != null)
            foreach (var a in customActions)
                actions.Add(a.AsString());

        var eventsSent = 0;
        var startTime = Time.GetTicksMsec();
        var durationMs = (ulong)(duration * 1000.0);

        while (Time.GetTicksMsec() - startTime < durationMs)
        {
            if (!ei.IsPlayingScene())
            {
                var elapsedNow = (Time.GetTicksMsec() - startTime) / 1000.0;
                return Success(new Dictionary
                {
                    ["completed"] = false,
                    ["crashed"] = true,
                    ["elapsed_seconds"] = elapsedNow,
                    ["events_sent"] = eventsSent,
                    ["error"] = "Game stopped during stress test",
                });
            }

            // Send a batch of random inputs
            var batch = new Godot.Collections.Array();
            for (var j = 0; j < 3; j++)
            {
                var actionName = actions[(int)(GD.Randi() % (uint)actions.Count)];
                batch.Add(new Dictionary
                {
                    ["type"] = "action",
                    ["action"] = actionName,
                    ["pressed"] = true,
                    ["strength"] = 1.0,
                });
                batch.Add(new Dictionary
                {
                    ["type"] = "action",
                    ["action"] = actionName,
                    ["pressed"] = false,
                    ["strength"] = 0.0,
                });
            }

            // Write input commands directly (same as input_commands)
            var json = Json.Stringify(new Dictionary { ["sequence_events"] = batch, ["frame_delay"] = 1 });
            using (var file = Godot.FileAccess.Open(GetGameUserDir() + "/mcp_input_commands", Godot.FileAccess.ModeFlags.Write))
            {
                if (file != null)
                {
                    file.StoreString(json);
                    eventsSent += batch.Count;
                }
            }

            await DelaySeconds(0.1);
        }

        var elapsed = (Time.GetTicksMsec() - startTime) / 1000.0;
        var finalErrors = CountLogErrors();
        var newErrors = finalErrors - initialErrors;

        // Check if game is still running
        var stillRunning = ei.IsPlayingScene();

        return Success(new Dictionary
        {
            ["completed"] = true,
            ["crashed"] = !stillRunning,
            ["duration_seconds"] = elapsed,
            ["events_sent"] = eventsSent,
            ["new_errors"] = newErrors,
            ["game_still_running"] = stillRunning,
        });
    }

    private Dictionary GetTestReport(Dictionary @params)
    {
        var clear = OptionalBool(@params, "clear", true);

        var passCount = 0;
        var failCount = 0;
        var details = new Godot.Collections.Array();

        foreach (var result in _testResults)
        {
            // Unwrap defensively and skip entries that carry no verdict
            // (input/wait/screenshot steps are not assertions).
            var entry = UnwrapGameResult(result);
            if (!entry.ContainsKey("passed"))
                continue;
            if (entry["passed"].AsBool())
                passCount++;
            else
                failCount++;
            details.Add(entry);
        }

        var total = passCount + failCount;
        var report = new Dictionary
        {
            ["total"] = total,
            ["passed"] = passCount,
            ["failed"] = failCount,
            ["pass_rate"] = total > 0 ? $"{100.0 * passCount / total:0.0}%" : "N/A",
            ["all_passed"] = failCount == 0 && total > 0,
            ["no_results"] = total == 0,
            ["details"] = details,
        };

        if (clear)
            _testResults.Clear();

        return Success(report);
    }

    // ── Step Executors (for run_test_scenario) ────────────────────────────────

    private Dictionary ExecuteInputStep(Dictionary step)
    {
        var events = new Godot.Collections.Array();

        if (step.ContainsKey("action"))
        {
            var pressed = OptionalBool(step, "pressed", true);
            var actionName = step["action"].AsString();
            events.Add(new Dictionary
            {
                ["type"] = "action",
                ["action"] = actionName,
                ["pressed"] = pressed,
                ["strength"] = OptionalFloat(step, "strength", 1.0),
            });
            // Auto-release if pressed
            if (pressed && OptionalBool(step, "auto_release", true))
            {
                events.Add(new Dictionary
                {
                    ["type"] = "action",
                    ["action"] = actionName,
                    ["pressed"] = false,
                    ["strength"] = 0.0,
                });
            }
        }
        else if (step.ContainsKey("keycode"))
        {
            var pressed = OptionalBool(step, "pressed", true);
            var keycode = step["keycode"].AsString();
            var shift = OptionalBool(step, "shift", false);
            var ctrl = OptionalBool(step, "ctrl", false);
            var alt = OptionalBool(step, "alt", false);
            events.Add(new Dictionary
            {
                ["type"] = "key",
                ["keycode"] = keycode,
                ["pressed"] = pressed,
                ["shift"] = shift,
                ["ctrl"] = ctrl,
                ["alt"] = alt,
            });
            // Auto-release if pressed, mirroring the action branch — otherwise the
            // key stays held for the rest of the session and corrupts later steps
            if (pressed && OptionalBool(step, "auto_release", true))
            {
                events.Add(new Dictionary
                {
                    ["type"] = "key",
                    ["keycode"] = keycode,
                    ["pressed"] = false,
                    ["shift"] = shift,
                    ["ctrl"] = ctrl,
                    ["alt"] = alt,
                });
            }
        }
        else
        {
            return new Dictionary { ["error"] = "Input step requires 'action' or 'keycode'" };
        }

        var json = Json.Stringify(new Dictionary
        {
            ["sequence_events"] = events,
            ["frame_delay"] = OptionalInt(step, "frame_delay", 1),
        });
        using var file = Godot.FileAccess.Open(GetGameUserDir() + "/mcp_input_commands", Godot.FileAccess.ModeFlags.Write);
        if (file == null)
            return new Dictionary { ["error"] = "Failed to write input commands" };
        file.StoreString(json);

        return new Dictionary { ["sent"] = true, ["event_count"] = events.Count };
    }

    private async Task<Dictionary> ExecuteWaitStep(Dictionary step)
    {
        if (step.ContainsKey("node_path"))
        {
            var nodePath = step["node_path"].AsString();
            var timeout = OptionalFloat(step, "timeout", 5.0);
            var result = await SendGameCommand("wait_for_node", new Dictionary
            {
                ["node_path"] = nodePath,
                ["timeout"] = timeout,
                ["poll_frames"] = OptionalInt(step, "poll_frames", 5),
            }, timeout + 2.0);
            if (result.ContainsKey("error"))
                return new Dictionary { ["error"] = $"Wait for node failed: {result["error"]}" };
            return new Dictionary { ["waited_for"] = nodePath, ["found"] = true };
        }

        var seconds = OptionalFloat(step, "seconds", 1.0);
        await DelaySeconds(seconds);
        return new Dictionary { ["waited_seconds"] = seconds };
    }

    private async Task<Dictionary> ExecuteAssertStep(Dictionary step)
    {
        if (step.ContainsKey("text"))
        {
            // Screen text assertion
            var uiResult = await SendGameCommand("find_ui_elements", new Dictionary());
            if (uiResult.ContainsKey("error"))
                return new Dictionary { ["passed"] = false, ["error"] = "Could not get UI elements" };

            var elements = ExtractElements(uiResult);
            var expectedText = step["text"].AsString();
            var partial = OptionalBool(step, "partial", true);

            // "assert_type" (not "type") so the caller's step_result merge cannot
            // collide with the step's own "type": "assert" key
            foreach (var elementVariant in elements)
            {
                if (elementVariant.VariantType != Variant.Type.Dictionary)
                    continue;
                var element = elementVariant.AsGodotDictionary();
                var elementText = element.TryGetValue("text", out var textVariant) ? textVariant.AsString() : "";
                if (partial && elementText.Contains(expectedText))
                    return new Dictionary { ["passed"] = true, ["assert_type"] = "screen_text", ["expected"] = expectedText, ["found_in"] = elementText };
                if (!partial && elementText == expectedText)
                    return new Dictionary { ["passed"] = true, ["assert_type"] = "screen_text", ["expected"] = expectedText, ["found_in"] = elementText };
            }

            return new Dictionary { ["passed"] = false, ["assert_type"] = "screen_text", ["expected"] = expectedText, ["error"] = "Text not found on screen" };
        }

        if (step.ContainsKey("node_path") && step.ContainsKey("property"))
        {
            // Node state assertion
            var nodePath = step["node_path"].AsString();
            var property = step["property"].AsString();
            var expected = step.TryGetValue("expected", out var expectedVariant) ? expectedVariant : new Variant();
            var operatorName = step.TryGetValue("operator", out var operatorVariant) ? operatorVariant.AsString() : "eq";

            var result = await SendGameCommand("assert_node_state", new Dictionary
            {
                ["node_path"] = nodePath,
                ["property"] = property,
                ["expected"] = expected,
                ["operator"] = operatorName,
            }, 5.0);
            if (result.ContainsKey("error"))
                return new Dictionary { ["passed"] = false, ["error"] = result["error"].ToString() };
            // Game replies are double-wrapped — unwrap until "passed" surfaces
            var payload = UnwrapGameResult(result);
            if (payload.ContainsKey("passed"))
                return payload;
            return new Dictionary { ["passed"] = false, ["error"] = "Unknown assertion error" };
        }

        return new Dictionary { ["passed"] = false, ["error"] = "Assert step requires 'text' or 'node_path'+'property'" };
    }

    // ── Utility ─────────────────────────────────────────────────────────────

    /// <summary>Extract the "elements" array from a find_ui_elements game response.</summary>
    private static Godot.Collections.Array ExtractElements(Dictionary uiResult)
    {
        if (uiResult.TryGetValue("result", out var resultVariant) && resultVariant.VariantType == Variant.Type.Dictionary)
        {
            var resultDict = resultVariant.AsGodotDictionary();
            if (resultDict.TryGetValue("elements", out var elementsVariant) && elementsVariant.VariantType == Variant.Type.Array)
                return elementsVariant.AsGodotArray();
        }
        return new Godot.Collections.Array();
    }

    /// <summary>Merge source keys into target without overwriting existing keys (matches GDScript's Dictionary.merge default).</summary>
    private static void MergeInto(Dictionary target, Dictionary source)
    {
        foreach (var key in source.Keys)
            if (!target.ContainsKey(key))
                target[key] = source[key];
    }

    /// <summary>Formats a string array the way GDScript's str() renders it, e.g. ["eq", "neq"].</summary>
    private static string FormatStringArray(string[] values) =>
        "[" + string.Join(", ", values.Select(v => $"\"{v}\"")) + "]";

    private static int CountLogErrors()
    {
        var count = 0;
        const string logPath = "user://logs/godot.log";
        if (Godot.FileAccess.FileExists(logPath))
        {
            using var file = Godot.FileAccess.Open(logPath, Godot.FileAccess.ModeFlags.Read);
            if (file != null)
            {
                var content = file.GetAsText();
                var lines = content.Split('\n');
                foreach (var line in lines)
                    if (line.Contains("ERROR") || line.Contains("SCRIPT ERROR"))
                        count++;
            }
        }
        return count;
    }
}
