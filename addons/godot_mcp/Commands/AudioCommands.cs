using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Audio tools: audio bus layout inspection and editing, bus effects, and
/// AudioStreamPlayer(2D/3D) node creation. Faithful port of
/// addons/godot_mcp/commands/audio_commands.gd. Scene mutations go through
/// EditorUndoRedoManager so they participate in the editor's undo history.
/// </summary>
[Tool]
public partial class AudioCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_audio_bus_layout",
            Description = "Get the full audio bus layout: every bus with its volume, solo/mute/bypass state, " +
                "send target, and each effect with its type and key parameters.",
            Category = "audio",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(GetAudioBusLayout(p)),
        },
        new()
        {
            Name = "add_audio_bus",
            Description = "Add a new audio bus to the AudioServer, optionally setting volume, send target, " +
                "solo and mute in the same call. Fails if a bus with the same name already exists.",
            Category = "audio",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["name"] = Schema.Str("Name for the new audio bus (e.g. 'SFX', 'Music')."),
                ["at_position"] = Schema.Int("Index to insert the bus at; -1 appends at the end.", -1),
                ["volume_db"] = Schema.Num("Initial bus volume in decibels."),
                ["send"] = Schema.Str("Name of the bus this bus sends its output to (e.g. 'Master')."),
                ["solo"] = Schema.Bool("Whether the bus starts soloed."),
                ["mute"] = Schema.Bool("Whether the bus starts muted."),
            }, "name"),
            Handler = p => Task.FromResult(AddAudioBus(p)),
        },
        new()
        {
            Name = "set_audio_bus",
            Description = "Modify an existing audio bus: volume, solo, mute, bypass_effects, send target, " +
                "and/or rename it.",
            Category = "audio",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["name"] = Schema.Str("Name of the audio bus to modify."),
                ["volume_db"] = Schema.Num("New bus volume in decibels."),
                ["solo"] = Schema.Bool("Set the bus solo state."),
                ["mute"] = Schema.Bool("Set the bus mute state."),
                ["bypass_effects"] = Schema.Bool("Set whether the bus bypasses its effects."),
                ["send"] = Schema.Str("New send target bus name."),
                ["rename"] = Schema.Str("New name for the bus."),
            }, "name"),
            Handler = p => Task.FromResult(SetAudioBus(p)),
        },
        new()
        {
            Name = "add_audio_bus_effect",
            Description = "Add an audio effect to a bus. Supported effect types: reverb, chorus, delay, " +
                "compressor, limiter, phaser, distortion, lowpassfilter, highpassfilter, bandpassfilter, " +
                "amplify, eq. Effect-specific parameters can be passed via 'params'.",
            Category = "audio",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["bus"] = Schema.Str("Name of the audio bus to add the effect to."),
                ["effect_type"] = Schema.Str("Effect type (case-insensitive): reverb, chorus, delay, compressor, " +
                    "limiter, phaser, distortion, lowpassfilter/lowpass, highpassfilter/highpass, " +
                    "bandpassfilter/bandpass, amplify, eq."),
                ["params"] = Schema.Obj("Effect-specific parameters, e.g. {\"room_size\": 0.8, \"wet\": 0.5} for reverb."),
                ["at_position"] = Schema.Int("Effect slot index to insert at; -1 appends at the end.", -1),
            }, "bus", "effect_type"),
            Handler = p => Task.FromResult(AddAudioBusEffect(p)),
        },
        new()
        {
            Name = "add_audio_player",
            Description = "Add an AudioStreamPlayer, AudioStreamPlayer2D, or AudioStreamPlayer3D node as a child " +
                "of an existing node, optionally assigning a stream resource, bus, volume and spatial settings.",
            Category = "audio",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the parent node."),
                ["name"] = Schema.Str("Name for the new audio player node."),
                ["type"] = Schema.Enum("Player node type (defaults to AudioStreamPlayer).",
                    "AudioStreamPlayer", "AudioStreamPlayer2D", "AudioStreamPlayer3D"),
                ["stream"] = Schema.Str("res:// path of an AudioStream resource to assign."),
                ["volume_db"] = Schema.Num("Player volume in decibels."),
                ["bus"] = Schema.Str("Name of the audio bus the player outputs to."),
                ["autoplay"] = Schema.Bool("Whether the player starts playing automatically."),
                ["max_distance"] = Schema.Num("2D/3D only: maximum audible distance."),
                ["attenuation"] = Schema.Num("2D only: attenuation exponent."),
                ["attenuation_model"] = Schema.Int("3D only: AttenuationModel enum value."),
                ["unit_size"] = Schema.Num("3D only: unit size factor for attenuation."),
            }, "node_path", "name"),
            Handler = p => Task.FromResult(AddAudioPlayer(p)),
        },
        new()
        {
            Name = "get_audio_info",
            Description = "List all AudioStreamPlayer/2D/3D nodes under a node (recursively) with their stream, " +
                "bus, volume, autoplay and spatial settings.",
            Category = "audio",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node to search from."),
            }, "node_path"),
            Handler = p => Task.FromResult(GetAudioInfo(p)),
        },
    };

    // ── get_audio_bus_layout ────────────────────────────────────────────────────

    private static Dictionary GetAudioBusLayout(Dictionary @params)
    {
        var buses = new Godot.Collections.Array();
        for (var i = 0; i < AudioServer.BusCount; i++)
        {
            var busData = new Dictionary
            {
                ["index"] = i,
                ["name"] = AudioServer.GetBusName(i),
                ["volume_db"] = AudioServer.GetBusVolumeDb(i),
                ["solo"] = AudioServer.IsBusSolo(i),
                ["mute"] = AudioServer.IsBusMute(i),
                ["bypass_effects"] = AudioServer.IsBusBypassingEffects(i),
                ["send"] = AudioServer.GetBusSend(i).ToString(),
                ["effects"] = new Godot.Collections.Array(),
            };
            var effects = new Godot.Collections.Array();
            for (var j = 0; j < AudioServer.GetBusEffectCount(i); j++)
            {
                var effect = AudioServer.GetBusEffect(i, j);
                var effectData = new Dictionary
                {
                    ["index"] = j,
                    ["type"] = effect.GetClass(),
                    ["enabled"] = AudioServer.IsBusEffectEnabled(i, j),
                };
                // Include effect-specific parameters
                effectData["params"] = GetEffectParams(effect);
                effects.Add(effectData);
            }
            busData["effects"] = effects;
            buses.Add(busData);
        }
        return Success(new Dictionary { ["bus_count"] = AudioServer.BusCount, ["buses"] = buses });
    }

    private static Dictionary GetEffectParams(AudioEffect effect)
    {
        var @params = new Dictionary();
        if (effect is AudioEffectReverb rev)
        {
            @params = new Dictionary
            {
                ["room_size"] = rev.RoomSize, ["damping"] = rev.Damping,
                ["wet"] = rev.Wet, ["dry"] = rev.Dry, ["spread"] = rev.Spread,
            };
        }
        else if (effect is AudioEffectDelay d)
        {
            @params = new Dictionary
            {
                ["tap1_active"] = d.Tap1Active, ["tap1_delay_ms"] = d.Tap1DelayMs, ["tap1_level_db"] = d.Tap1LevelDb,
                ["tap2_active"] = d.Tap2Active, ["tap2_delay_ms"] = d.Tap2DelayMs, ["tap2_level_db"] = d.Tap2LevelDb,
            };
        }
        else if (effect is AudioEffectCompressor c)
        {
            @params = new Dictionary
            {
                ["threshold"] = c.Threshold, ["ratio"] = c.Ratio, ["attack_us"] = c.AttackUs,
                ["release_ms"] = c.ReleaseMs, ["gain"] = c.Gain, ["mix"] = c.Mix,
                ["sidechain"] = c.Sidechain.ToString(),
            };
        }
        // AudioEffectLimiter is deprecated in Godot 4.3+, but the GDScript original
        // still supports it, so the port keeps parity.
#pragma warning disable CS0618
        else if (effect is AudioEffectLimiter l)
        {
            @params = new Dictionary
            {
                ["ceiling_db"] = l.CeilingDb, ["threshold_db"] = l.ThresholdDb,
                ["soft_clip_db"] = l.SoftClipDb, ["soft_clip_ratio"] = l.SoftClipRatio,
            };
        }
        else if (effect is AudioEffectDistortion dist)
        {
            @params = new Dictionary
            {
                ["mode"] = (long)dist.Mode, ["pre_gain"] = dist.PreGain, ["post_gain"] = dist.PostGain,
                ["keep_hf_hz"] = dist.KeepHfHz, ["drive"] = dist.Drive,
            };
        }
        else if (effect is AudioEffectChorus ch)
        {
            @params = new Dictionary
            {
                ["voice_count"] = ch.VoiceCount, ["dry"] = ch.Dry, ["wet"] = ch.Wet,
            };
        }
        else if (effect is AudioEffectPhaser ph)
        {
            @params = new Dictionary
            {
                ["range_min_hz"] = ph.RangeMinHz, ["range_max_hz"] = ph.RangeMaxHz,
                ["rate_hz"] = ph.RateHz, ["feedback"] = ph.Feedback, ["depth"] = ph.Depth,
            };
        }
        else if (effect is AudioEffectFilter f)
        {
            // Covers LowPassFilter, HighPassFilter, BandPassFilter, etc.
            @params = new Dictionary
            {
                ["cutoff_hz"] = f.CutoffHz, ["resonance"] = f.Resonance,
                ["gain"] = f.Gain, ["db"] = (long)f.Db,
            };
        }
        else if (effect is AudioEffectAmplify a)
        {
            @params = new Dictionary { ["volume_db"] = a.VolumeDb };
        }
        return @params;
    }

    // ── add_audio_bus ───────────────────────────────────────────────────────────

    private static Dictionary AddAudioBus(Dictionary @params)
    {
        var (busName, err) = RequireString(@params, "name");
        if (err != null)
            return err;

        // Check if bus name already exists
        for (var i = 0; i < AudioServer.BusCount; i++)
        {
            if (AudioServer.GetBusName(i) == busName)
                return ErrorInvalidParams($"Audio bus '{busName}' already exists at index {i}");
        }

        var atPosition = OptionalInt(@params, "at_position", -1);
        AudioServer.AddBus(atPosition);

        var idx = atPosition < 0 ? AudioServer.BusCount - 1 : atPosition;
        AudioServer.SetBusName(idx, busName);

        if (@params.ContainsKey("volume_db"))
            AudioServer.SetBusVolumeDb(idx, (float)OptionalFloat(@params, "volume_db"));

        var send = OptionalString(@params, "send", "");
        if (!string.IsNullOrEmpty(send))
            AudioServer.SetBusSend(idx, send);

        if (@params.ContainsKey("solo"))
            AudioServer.SetBusSolo(idx, CoerceBool(@params["solo"]));

        if (@params.ContainsKey("mute"))
            AudioServer.SetBusMute(idx, CoerceBool(@params["mute"]));

        return Success(new Dictionary
        {
            ["name"] = busName,
            ["index"] = idx,
            ["bus_count"] = AudioServer.BusCount,
        });
    }

    // ── set_audio_bus ───────────────────────────────────────────────────────────

    private static Dictionary SetAudioBus(Dictionary @params)
    {
        var (busName, err) = RequireString(@params, "name");
        if (err != null)
            return err;

        var idx = AudioServer.GetBusIndex(busName);
        if (idx < 0)
            return ErrorNotFound($"Audio bus '{busName}'");

        var changes = 0;

        if (@params.ContainsKey("volume_db"))
        {
            AudioServer.SetBusVolumeDb(idx, (float)OptionalFloat(@params, "volume_db"));
            changes++;
        }

        if (@params.ContainsKey("solo"))
        {
            AudioServer.SetBusSolo(idx, CoerceBool(@params["solo"]));
            changes++;
        }

        if (@params.ContainsKey("mute"))
        {
            AudioServer.SetBusMute(idx, CoerceBool(@params["mute"]));
            changes++;
        }

        if (@params.ContainsKey("bypass_effects"))
        {
            AudioServer.SetBusBypassEffects(idx, CoerceBool(@params["bypass_effects"]));
            changes++;
        }

        var send = OptionalString(@params, "send", "");
        if (!string.IsNullOrEmpty(send))
        {
            AudioServer.SetBusSend(idx, send);
            changes++;
        }

        if (@params.ContainsKey("rename"))
        {
            var newName = @params["rename"].VariantType == Variant.Type.String
                ? @params["rename"].AsString()
                : @params["rename"].ToString();
            AudioServer.SetBusName(idx, newName);
            busName = newName;
            changes++;
        }

        return Success(new Dictionary { ["name"] = busName, ["index"] = idx, ["changes"] = changes });
    }

    // ── add_audio_bus_effect ────────────────────────────────────────────────────

    private static Dictionary AddAudioBusEffect(Dictionary @params)
    {
        var (busName, err) = RequireString(@params, "bus");
        if (err != null)
            return err;

        var (effectType, err2) = RequireString(@params, "effect_type");
        if (err2 != null)
            return err2;

        var busIdx = AudioServer.GetBusIndex(busName);
        if (busIdx < 0)
            return ErrorNotFound($"Audio bus '{busName}'");

        AudioEffect effect;
        var effectParams = OptionalDict(@params, "params") ?? new Dictionary();

        switch (effectType.ToLowerInvariant())
        {
            case "reverb":
            {
                var e = new AudioEffectReverb();
                if (effectParams.ContainsKey("room_size"))
                    e.RoomSize = (float)OptionalFloat(effectParams, "room_size");
                if (effectParams.ContainsKey("damping"))
                    e.Damping = (float)OptionalFloat(effectParams, "damping");
                if (effectParams.ContainsKey("wet"))
                    e.Wet = (float)OptionalFloat(effectParams, "wet");
                if (effectParams.ContainsKey("dry"))
                    e.Dry = (float)OptionalFloat(effectParams, "dry");
                if (effectParams.ContainsKey("spread"))
                    e.Spread = (float)OptionalFloat(effectParams, "spread");
                effect = e;
                break;
            }
            case "chorus":
            {
                var e = new AudioEffectChorus();
                if (effectParams.ContainsKey("voice_count"))
                    e.VoiceCount = OptionalInt(effectParams, "voice_count");
                if (effectParams.ContainsKey("dry"))
                    e.Dry = (float)OptionalFloat(effectParams, "dry");
                if (effectParams.ContainsKey("wet"))
                    e.Wet = (float)OptionalFloat(effectParams, "wet");
                effect = e;
                break;
            }
            case "delay":
            {
                var e = new AudioEffectDelay();
                if (effectParams.ContainsKey("tap1_active"))
                    e.Tap1Active = CoerceBool(effectParams["tap1_active"]);
                if (effectParams.ContainsKey("tap1_delay_ms"))
                    e.Tap1DelayMs = (float)OptionalFloat(effectParams, "tap1_delay_ms");
                if (effectParams.ContainsKey("tap1_level_db"))
                    e.Tap1LevelDb = (float)OptionalFloat(effectParams, "tap1_level_db");
                if (effectParams.ContainsKey("tap2_active"))
                    e.Tap2Active = CoerceBool(effectParams["tap2_active"]);
                if (effectParams.ContainsKey("tap2_delay_ms"))
                    e.Tap2DelayMs = (float)OptionalFloat(effectParams, "tap2_delay_ms");
                if (effectParams.ContainsKey("tap2_level_db"))
                    e.Tap2LevelDb = (float)OptionalFloat(effectParams, "tap2_level_db");
                effect = e;
                break;
            }
            case "compressor":
            {
                var e = new AudioEffectCompressor();
                if (effectParams.ContainsKey("threshold"))
                    e.Threshold = (float)OptionalFloat(effectParams, "threshold");
                if (effectParams.ContainsKey("ratio"))
                    e.Ratio = (float)OptionalFloat(effectParams, "ratio");
                if (effectParams.ContainsKey("attack_us"))
                    e.AttackUs = (float)OptionalFloat(effectParams, "attack_us");
                if (effectParams.ContainsKey("release_ms"))
                    e.ReleaseMs = (float)OptionalFloat(effectParams, "release_ms");
                if (effectParams.ContainsKey("gain"))
                    e.Gain = (float)OptionalFloat(effectParams, "gain");
                if (effectParams.ContainsKey("mix"))
                    e.Mix = (float)OptionalFloat(effectParams, "mix");
                effect = e;
                break;
            }
            case "limiter":
            {
                var e = new AudioEffectLimiter();
                if (effectParams.ContainsKey("ceiling_db"))
                    e.CeilingDb = (float)OptionalFloat(effectParams, "ceiling_db");
                if (effectParams.ContainsKey("threshold_db"))
                    e.ThresholdDb = (float)OptionalFloat(effectParams, "threshold_db");
                if (effectParams.ContainsKey("soft_clip_db"))
                    e.SoftClipDb = (float)OptionalFloat(effectParams, "soft_clip_db");
                if (effectParams.ContainsKey("soft_clip_ratio"))
                    e.SoftClipRatio = (float)OptionalFloat(effectParams, "soft_clip_ratio");
                effect = e;
                break;
            }
            case "phaser":
            {
                var e = new AudioEffectPhaser();
                if (effectParams.ContainsKey("range_min_hz"))
                    e.RangeMinHz = (float)OptionalFloat(effectParams, "range_min_hz");
                if (effectParams.ContainsKey("range_max_hz"))
                    e.RangeMaxHz = (float)OptionalFloat(effectParams, "range_max_hz");
                if (effectParams.ContainsKey("rate_hz"))
                    e.RateHz = (float)OptionalFloat(effectParams, "rate_hz");
                if (effectParams.ContainsKey("feedback"))
                    e.Feedback = (float)OptionalFloat(effectParams, "feedback");
                if (effectParams.ContainsKey("depth"))
                    e.Depth = (float)OptionalFloat(effectParams, "depth");
                effect = e;
                break;
            }
            case "distortion":
            {
                var e = new AudioEffectDistortion();
                if (effectParams.ContainsKey("mode"))
                    e.Mode = (AudioEffectDistortion.ModeEnum)OptionalInt(effectParams, "mode");
                if (effectParams.ContainsKey("pre_gain"))
                    e.PreGain = (float)OptionalFloat(effectParams, "pre_gain");
                if (effectParams.ContainsKey("post_gain"))
                    e.PostGain = (float)OptionalFloat(effectParams, "post_gain");
                if (effectParams.ContainsKey("keep_hf_hz"))
                    e.KeepHfHz = (float)OptionalFloat(effectParams, "keep_hf_hz");
                if (effectParams.ContainsKey("drive"))
                    e.Drive = (float)OptionalFloat(effectParams, "drive");
                effect = e;
                break;
            }
            case "lowpassfilter":
            case "lowpass":
            {
                var e = new AudioEffectLowPassFilter();
                if (effectParams.ContainsKey("cutoff_hz"))
                    e.CutoffHz = (float)OptionalFloat(effectParams, "cutoff_hz");
                if (effectParams.ContainsKey("resonance"))
                    e.Resonance = (float)OptionalFloat(effectParams, "resonance");
                effect = e;
                break;
            }
            case "highpassfilter":
            case "highpass":
            {
                var e = new AudioEffectHighPassFilter();
                if (effectParams.ContainsKey("cutoff_hz"))
                    e.CutoffHz = (float)OptionalFloat(effectParams, "cutoff_hz");
                if (effectParams.ContainsKey("resonance"))
                    e.Resonance = (float)OptionalFloat(effectParams, "resonance");
                effect = e;
                break;
            }
            case "bandpassfilter":
            case "bandpass":
            {
                var e = new AudioEffectBandPassFilter();
                if (effectParams.ContainsKey("cutoff_hz"))
                    e.CutoffHz = (float)OptionalFloat(effectParams, "cutoff_hz");
                if (effectParams.ContainsKey("resonance"))
                    e.Resonance = (float)OptionalFloat(effectParams, "resonance");
                effect = e;
                break;
            }
            case "amplify":
            {
                var e = new AudioEffectAmplify();
                if (effectParams.ContainsKey("volume_db"))
                    e.VolumeDb = (float)OptionalFloat(effectParams, "volume_db");
                effect = e;
                break;
            }
            case "eq":
            {
                effect = new AudioEffectEQ();
                break;
            }
            default:
                return ErrorInvalidParams(
                    $"Unknown effect type: '{effectType}'. Valid types: reverb, chorus, delay, compressor, " +
                    "limiter, phaser, distortion, lowpassfilter, highpassfilter, bandpassfilter, amplify, eq");
        }

        var atPosition = OptionalInt(@params, "at_position", -1);
        AudioServer.AddBusEffect(busIdx, effect, atPosition);

        var effectIdx = atPosition < 0 ? AudioServer.GetBusEffectCount(busIdx) - 1 : atPosition;
        return Success(new Dictionary
        {
            ["bus"] = busName,
            ["bus_index"] = busIdx,
            ["effect_type"] = effect.GetClass(),
            ["effect_index"] = effectIdx,
        });
    }

    // ── add_audio_player ────────────────────────────────────────────────────────

    private Dictionary AddAudioPlayer(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var (playerName, err2) = RequireString(@params, "name");
        if (err2 != null)
            return err2;

        var playerType = OptionalString(@params, "type", "AudioStreamPlayer");
        var validTypes = new[] { "AudioStreamPlayer", "AudioStreamPlayer2D", "AudioStreamPlayer3D" };
        if (System.Array.IndexOf(validTypes, playerType) < 0)
            return ErrorInvalidParams($"Invalid player type '{playerType}'. Valid: {string.Join(", ", validTypes)}");

        var parent = FindNodeByPath(nodePath);
        if (parent == null)
            return ErrorNotFound($"Node at '{nodePath}'");
        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        Node player = playerType switch
        {
            "AudioStreamPlayer2D" => new AudioStreamPlayer2D(),
            "AudioStreamPlayer3D" => new AudioStreamPlayer3D(),
            _ => new AudioStreamPlayer(),
        };

        player.Name = playerName;

        // Set stream if provided
        var streamPath = OptionalString(@params, "stream", "");
        if (!string.IsNullOrEmpty(streamPath))
        {
            if (ResourceLoader.Exists(streamPath))
            {
                var stream = ResourceLoader.Load(streamPath);
                if (stream is AudioStream)
                {
                    player.Set("stream", stream);
                }
                else
                {
                    player.QueueFree();
                    return ErrorInvalidParams($"Resource at '{streamPath}' is not an AudioStream");
                }
            }
            else
            {
                player.QueueFree();
                return ErrorNotFound($"Audio stream at '{streamPath}'");
            }
        }

        // Common properties
        if (@params.ContainsKey("volume_db"))
            player.Set("volume_db", (float)OptionalFloat(@params, "volume_db"));

        var bus = OptionalString(@params, "bus", "");
        if (!string.IsNullOrEmpty(bus))
            player.Set("bus", bus);

        if (@params.ContainsKey("autoplay"))
            player.Set("autoplay", CoerceBool(@params["autoplay"]));

        // 2D-specific properties
        if (player is AudioStreamPlayer2D player2D)
        {
            if (@params.ContainsKey("max_distance"))
                player2D.MaxDistance = (float)OptionalFloat(@params, "max_distance");
            if (@params.ContainsKey("attenuation"))
                player2D.Attenuation = (float)OptionalFloat(@params, "attenuation");
        }

        // 3D-specific properties
        if (player is AudioStreamPlayer3D player3D)
        {
            if (@params.ContainsKey("max_distance"))
                player3D.MaxDistance = (float)OptionalFloat(@params, "max_distance");
            if (@params.ContainsKey("attenuation_model"))
                player3D.AttenuationModel = (AudioStreamPlayer3D.AttenuationModelEnum)OptionalInt(@params, "attenuation_model");
            if (@params.ContainsKey("unit_size"))
                player3D.UnitSize = (float)OptionalFloat(@params, "unit_size");
        }

        AddChildWithUndo(parent, player, root, "MCP: Add audio player");

        return Success(new Dictionary
        {
            ["name"] = playerName,
            ["type"] = playerType,
            ["parent"] = nodePath,
            ["stream"] = streamPath,
            ["bus"] = player.Get("bus"),
            ["volume_db"] = player.Get("volume_db"),
            ["autoplay"] = player.Get("autoplay"),
        });
    }

    // ── get_audio_info ──────────────────────────────────────────────────────────

    private static Dictionary GetAudioInfo(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");

        var players = new Godot.Collections.Array();
        CollectAudioPlayers(node, players);

        return Success(new Dictionary
        {
            ["node_path"] = nodePath,
            ["audio_player_count"] = players.Count,
            ["players"] = players,
        });
    }

    private static void CollectAudioPlayers(Node node, Godot.Collections.Array result)
    {
        if (node is AudioStreamPlayer or AudioStreamPlayer2D or AudioStreamPlayer3D)
        {
            var info = new Dictionary
            {
                ["name"] = node.Name,
                ["path"] = GetEditedRoot()!.GetPathTo(node).ToString(),
                ["type"] = node.GetClass(),
                ["volume_db"] = node.Get("volume_db"),
                ["bus"] = node.Get("bus"),
                ["autoplay"] = node.Get("autoplay"),
                ["playing"] = node.Get("playing"),
                ["stream"] = "",
            };
            var stream = node.Get("stream");
            if (stream.VariantType == Variant.Type.Object && stream.AsGodotObject() is AudioStream audioStream)
                info["stream"] = audioStream.ResourcePath;

            if (node is AudioStreamPlayer2D player2D)
            {
                info["max_distance"] = player2D.MaxDistance;
                info["attenuation"] = player2D.Attenuation;
            }
            else if (node is AudioStreamPlayer3D player3D)
            {
                info["max_distance"] = player3D.MaxDistance;
                info["attenuation_model"] = (long)player3D.AttenuationModel;
                info["unit_size"] = player3D.UnitSize;
            }

            result.Add(info);
        }

        foreach (var child in node.GetChildren())
            CollectAudioPlayers(child, result);
    }
}
