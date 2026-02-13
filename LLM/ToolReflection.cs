using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

internal sealed class ToolMethodMetadata {
    public MethodInfo Method { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public ToolVisibility Visibility { get; init; }
    public List<ParamMetadata> Params { get; init; } = new();
    public List<MemberInfo> Guards { get; init; } = new();
    public bool DisallowSelfTarget { get; init; }
}

internal sealed class ParamMetadata {
    public ParameterInfo ParameterInfo { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public bool Required { get; init; }
    public Type ClrType { get; init; }
    public bool IsTargetId { get; init; }
}

internal static class ToolMetadataCache {
    private static readonly ConcurrentDictionary<Type, List<ToolMethodMetadata>> _cache = new();

    public static List<ToolMethodMetadata> GetForType(Type type) {
        return _cache.GetOrAdd(type, BuildForType);
    }

    private static List<ToolMethodMetadata> BuildForType(Type type) {
        var list = new List<ToolMethodMetadata>();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var m in methods) {
            var toolAttr = m.GetCustomAttribute<ToolAttribute>();
            if (toolAttr == null) continue;

            // enforce return type: Task<ToolCallResult>
            var ret = m.ReturnType;
            var okAsync = ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>) && ret.GetGenericArguments()[0] == typeof(ToolCallResult);
            if (!okAsync) {
                GD.PushError($"[ToolMetadata] Method {type.Name}.{m.Name} must return Task<ToolCallResult>.");
                continue;
            }

            var meta = new ToolMethodMetadata {
                Method = m,
                Name = toolAttr.Name ?? ToCamelCase(m.Name),
                Description = toolAttr.Description,
                Visibility = toolAttr.Visibility,
                Params = new List<ParamMetadata>(),
                Guards = new List<MemberInfo>(),
                DisallowSelfTarget = m.IsDefined(typeof(DisallowSelfTargetAttribute), inherit: false)
            };

            // parameters
            foreach (var p in m.GetParameters()) {
                if (p.ParameterType == typeof(ToolCallContext)) continue; // injected
                var arg = p.GetCustomAttribute<ToolArgAttribute>();
                if (arg == null) {
                    GD.PushError($"[ToolMetadata] Parameter '{p.Name}' on {type.Name}.{m.Name} must have [ToolArg].");
                    continue;
                }
                meta.Params.Add(new ParamMetadata {
                    ParameterInfo = p,
                    Name = arg.Name,
                    Description = arg.Description,
                    Required = arg.Required,
                    ClrType = p.ParameterType,
                    IsTargetId = false
                });
            }

            // guards
            var guardAttrs = m.GetCustomAttributes<ToolWhenAttribute>()?.ToArray() ?? Array.Empty<ToolWhenAttribute>();
            foreach (var g in guardAttrs) {
                var member = type.GetMember(g.GuardMemberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault();
                if (member == null) {
                    GD.PushError($"[ToolMetadata] Guard member '{g.GuardMemberName}' not found on {type.Name}.");
                    continue;
                }
                meta.Guards.Add(member);
            }

            list.Add(meta);
        }
        return list;
    }

    private static string ToCamelCase(string s) {
        if (string.IsNullOrEmpty(s)) return s;
        if (char.IsLower(s[0])) return s;
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }
}

internal static class ToolSchemaBuilder {
    private static readonly float DefaultProximityMeters = AgenticConfig.GetValue("TOOL_DEFAULT_PROXIMITY_METERS", 5.0f);

    public static List<Tool> BuildSchemas(Node interactable, ToolCallContext ctx, bool ignoreProximity = false) {
        var type = interactable.GetType();
        var metas = ToolMetadataCache.GetForType(type);
        var tools = new List<Tool>();
        foreach (var meta in metas) {
            if (!IsVisibleToCaller(meta, interactable, ctx, ignoreProximity)) continue;
            if (!EvaluateGuards(meta, interactable, ctx)) continue;

            var properties = new List<Property>();
            var required = new List<string>();


	            // Safety: prevent private tools from declaring an arg named 'targetId'.
	            // 'targetId' is reserved for routing (public tools only). Using it on a private tool
	            // would cause TargetResolution to mis-route the call as outbound.
	            if (meta.Visibility == ToolVisibility.Private && meta.Params.Any(p => string.Equals(p.Name, "targetId", StringComparison.Ordinal))) {
	                GD.PushError($"[ToolSchema] Private tool '{type.Name}.{meta.Name}' must not declare an argument named 'targetId' (reserved for routing). Rename the parameter (e.g., 'destination', 'shelfId').");
	            }

            // Public tools are routable and require targetId to be exposed
            if (meta.Visibility == ToolVisibility.Public) {
                properties.Add(LLMTool.Property("targetId", "string", "The target object's ID."));
                required.Add("targetId");
            }

            foreach (var p in meta.Params) {
                var desc = p.Description;
                if (p.ClrType == typeof(TargetResolutionWaypoint)) {
                    desc = string.IsNullOrEmpty(desc) ? "Must be a valid target ID." : desc + " (Must be a valid target ID.)";
                }
                properties.Add(LLMTool.Property(p.Name, MapType(p.ClrType), desc));
                if (p.Required) required.Add(p.Name);
            }

            var tool = LLMTool.Function(
                name: meta.Name,
                description: meta.Description,
                parameters: LLMTool.Parameters(properties.ToArray()),
                required: required
            );
            tools.Add(tool);
        }
        return tools;
    }

    private static bool IsVisibleToCaller(ToolMethodMetadata meta, Node interactable, ToolCallContext ctx, bool ignoreProximity) {
        if (meta.Visibility == ToolVisibility.Private) {
            var ownerEntity = interactable.GetBelongingEntity<Node>();
            return ownerEntity == ctx?.SourceEntity; // owner-only
        }
        // Public: optionally hide when caller targets self if requested by attribute
        if (meta.Visibility == ToolVisibility.Public && meta.DisallowSelfTarget) {
            var src = ctx?.SourceEntity;
            var dst = ctx?.TargetWaypoint?.GetBelongingEntity<Node>();
            if (src != null && dst != null && src == dst) return false;
        }

        // Listing-time proximity filter for public tools (execution-time still enforces proximity too).
        // This prevents the LLM from being shown tools it can't currently reach.
        if (!ignoreProximity
            && meta.Visibility == ToolVisibility.Public
            && !meta.Method.IsDefined(typeof(NoProximityRequiredAttribute), inherit: false)) {

            var proxAttr = meta.Method.GetCustomAttribute<RequiresProximityAttribute>();
            float requiredRadius = proxAttr != null ? proxAttr.RadiusMeters : DefaultProximityMeters;
            if (requiredRadius > 0f) {
                var src3D = ctx?.SourceEntity?.GetBelongingEntity<Node3D>();
                var wp = ctx?.TargetWaypoint;
                if (GodotObject.IsInstanceValid(src3D) && GodotObject.IsInstanceValid(wp)) {
                    var dist = src3D.GlobalPosition.DistanceTo(wp.GetPosition());
                    if (dist > requiredRadius) return false;
                }
            }
        }
        return true; // Public (visible by default)
    }

    private static bool EvaluateGuards(ToolMethodMetadata meta, Node target, ToolCallContext ctx) {
        foreach (var member in meta.Guards) {
            bool pass = false;
            switch (member) {
                case MethodInfo mi when mi.GetParameters().Length == 0 && mi.ReturnType == typeof(bool):
                    pass = (bool)mi.Invoke(target, null);
                    break;
                case MethodInfo mi2 when mi2.GetParameters().Length == 1 && mi2.GetParameters()[0].ParameterType == typeof(ToolCallContext) && mi2.ReturnType == typeof(bool):
                    pass = (bool)mi2.Invoke(target, new object[] { ctx });
                    break;
                case PropertyInfo pi when pi.PropertyType == typeof(bool):
                    pass = (bool)pi.GetValue(target);
                    break;
                default:
                    GD.PushError($"[ToolGuards] Unsupported guard signature on {target.GetType().Name}.{member.Name}");
                    pass = false;
                    break;
            }
            if (!pass) return false;
        }
        return true;
    }


    private static string MapType(Type t) {
        if (t == typeof(string)) return "string";
        if (t == typeof(int) || t == typeof(long)) return "string"; // we prompt via string, validate in handler
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "string";
        if (t == typeof(bool)) return "string";
        return "string"; // default to string for UI; handler validates/convert
    }
}

internal static class ToolInvocation {
    public static async Task<ToolCallResult> InvokeAsync(Node target, ToolCall toolCall, ToolCallContext ctx) {
        var meta = ToolMetadataCache.GetForType(target.GetType()).FirstOrDefault(m => m.Name == toolCall.Function.Name);
        if (meta == null) return Results.FailText($"Tool '{toolCall.Function.Name}' not supported by {target.GetType().Name}.", "unsupported");

        // Visibility and guards re-check at call time
        if (!ToolSchemaBuilder.BuildSchemas(target, ctx, ignoreProximity: true).Any(t => t.Function.Name == meta.Name)) {
            return Results.FailText($"Tool '{meta.Name}' not available in current state.", "unavailable");
        }

        // Disallow self-target at call time if requested (defense-in-depth)
        if (meta.Visibility == ToolVisibility.Public && meta.DisallowSelfTarget) {
            var src = ctx?.SourceEntity;
            var dst = ctx?.TargetWaypoint?.GetBelongingEntity<Node>();
            if (src != null && dst != null && src == dst) {
                return Results.FailText("You cannot target yourself.", "self_target");
            }
        }

        // Build argument list
        var args = new List<object>();
        TargetResolutionWaypoint firstWaypointArg = null;
        foreach (var p in meta.Method.GetParameters()) {
            if (p.ParameterType == typeof(ToolCallContext)) { args.Add(ctx); continue; }
            var argAttr = p.GetCustomAttribute<ToolArgAttribute>();
            if (argAttr == null) {
                return Results.FailText($"Parameter '{p.Name}' on {target.GetType().Name}.{meta.Method.Name} missing [ToolArg].", "bad_param");
            }

            // Waypoint param: accept string IDs outward, resolve to TargetResolutionWaypoint internally
            if (p.ParameterType == typeof(TargetResolutionWaypoint)) {
                var value = ExtractArg(toolCall.Function.Arguments, argAttr.Name);
                if (value == null) {
                    if (argAttr.Required) return Results.FailText($"Missing required argument '{argAttr.Name}'.", "missing_argument");
                    args.Add(null);
                } else {
                    var id = value.ToString();
                    var (exact, waypoint) = TargetResolution.Instance.ResolveTargetFuzzy(id);
                    if (!GodotObject.IsInstanceValid(waypoint)) {
                        return Results.FailText($"Could not resolve parameter '{argAttr.Name}' with passed argument '{id}' to a valid target. Please refer to the list of valid target resolution waypoints.", "target_not_found");
                    }
                    args.Add(waypoint);
                    if (firstWaypointArg == null) firstWaypointArg = waypoint;
                }
                continue;
            }

            var raw = ExtractArg(toolCall.Function.Arguments, argAttr.Name);
            if (raw == null) {
                if (argAttr.Required) return Results.FailText($"Missing required argument '{argAttr.Name}'.", "missing_argument");
                args.Add(GetDefault(p.ParameterType));
            } else {
                var (ok, converted, err) = ConvertArg(raw, p.ParameterType);
                if (!ok) return Results.FailText($"Argument '{argAttr.Name}' has invalid type: {err}", "bad_argument");
                args.Add(converted);
            }
        }

        // Execution-time proximity guard (default for public tools; opt-out via [NoProximityRequired])
        bool enforceProx = meta.Visibility == ToolVisibility.Public
            && !meta.Method.IsDefined(typeof(NoProximityRequiredAttribute), inherit: false);

        float defaultRadius = AgenticConfig.GetValue("TOOL_DEFAULT_PROXIMITY_METERS", 5.0f);
        var proxAttr = meta.Method.GetCustomAttribute<RequiresProximityAttribute>();
        float requiredRadius = proxAttr != null ? proxAttr.RadiusMeters : defaultRadius;

        if (enforceProx && requiredRadius > 0f) {
            var src3D = ctx?.SourceEntity?.GetBelongingEntity<Node3D>();
            if (!GodotObject.IsInstanceValid(src3D)) {
                return Results.FailText("Cannot determine caller position to check proximity.", "no_source_position");
            }
            var wp = ctx?.TargetWaypoint ?? firstWaypointArg; // prefer context target, otherwise the first waypoint arg
            if (!GodotObject.IsInstanceValid(wp)) {
                return Results.FailText("No spatial target available to check proximity.", "non_spatial_target");
            }
            var dist = src3D.GlobalPosition.DistanceTo(wp.GetPosition());
            if (dist > requiredRadius) {
                return Results.FailText($"Too far away ({dist:0.0}m > {requiredRadius:0.0}m). Move closer and try again.", "out_of_range");
            }
        }

        var resultTask = (Task<ToolCallResult>)meta.Method.Invoke(target, args.ToArray());
        return await resultTask.ConfigureAwait(false);
    }

    private static JsonNode ExtractArg(JsonNode args, string name) {
        if (args == null) return null;
        var obj = args.AsObject();
        return obj.ContainsKey(name) ? obj[name] : null;
    }

    private static (bool ok, object value, string err) ConvertArg(JsonNode node, Type targetType) {
        try {
            if (targetType == typeof(string)) return (true, node.ToString(), null);
            if (targetType == typeof(int)) return (int.TryParse(node.ToString(), out var i) ? (true, i, null) : (false, null, "expected integer"));
            if (targetType == typeof(long)) return (long.TryParse(node.ToString(), out var l) ? (true, l, null) : (false, null, "expected long"));
            if (targetType == typeof(float)) return (float.TryParse(node.ToString(), out var f) ? (true, f, null) : (false, null, "expected float"));
            if (targetType == typeof(double)) return (double.TryParse(node.ToString(), out var d) ? (true, d, null) : (false, null, "expected double"));
            if (targetType == typeof(decimal)) return (decimal.TryParse(node.ToString(), out var m) ? (true, m, null) : (false, null, "expected decimal"));
            if (targetType == typeof(bool)) return (bool.TryParse(node.ToString(), out var b) ? (true, b, null) : (false, null, "expected bool"));
            // fallback: string
            return (true, node.ToString(), null);
        } catch (Exception e) {
            return (false, null, e.Message);
        }
    }

    private static object GetDefault(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;
}
