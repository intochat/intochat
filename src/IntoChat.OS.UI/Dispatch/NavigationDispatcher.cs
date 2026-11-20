using System.Reflection;
using System.Text.Json;
using DigitalBrain.Invocation;

namespace IntoChat.OS.UI;
internal sealed class NavigationDispatchTable(NavigationDispatchTable.TargetEntry[] targets)
{
    public TargetEntry[] Targets { get; } = targets;

    public static NavigationDispatchTable Build(Type screenType)
    {
        var entries = screenType
            .GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INavigatedTo<>))
            .Select(iface =>
            {
                var argsType = iface.GetGenericArguments()[0];
                var arrivalType = typeof(NavigationArrival<>).MakeGenericType(argsType);
                var map = screenType.GetInterfaceMap(iface);
                var method = map.TargetMethods[0];
                var options = new JsonSerializerOptions(RuntimeProtocol.OptionsFor(argsType))
                {
                    PropertyNameCaseInsensitive = true,
                };
                return new TargetEntry(argsType, arrivalType, method, options);
            })
            .ToArray();
        return new NavigationDispatchTable(entries);
    }

    public async Task InvokeJsonAsync(
        object screen,
        string? argsJson,
        string? fromNeuron,
        string transition,
        string? correlationId,
        CancellationToken cancellationToken,
        string? input = null)
    {
        var entry = Targets[0];
        var argsValue = DeserializeArgs(argsJson, input, entry);
        var arrival = Activator.CreateInstance(entry.ArrivalType, argsValue, fromNeuron, transition, correlationId)!;
        object? rawTask;
        try
        {
            rawTask = entry.Method.Invoke(screen, [arrival, cancellationToken]);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }

        if (rawTask is Task task)
        {
            await task.ConfigureAwait(true);
        }
    }

    private static object? DeserializeArgs(string? argsJson, string? input, TargetEntry entry)
    {
        if (entry.ArgsType == typeof(string))
        {
            if (!string.IsNullOrEmpty(argsJson))
            {
                return ExtractScalarString(argsJson.Trim(), entry.JsonOptions);
            }

            return input ?? string.Empty;
        }

        var trimmedJson = argsJson?.Trim();
        if (!string.IsNullOrEmpty(trimmedJson))
        {
            if (trimmedJson.StartsWith('{'))
            {
                if (string.Equals(trimmedJson, "{}", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(input))
                {
                    return CoercePlainInput(input.Trim(), entry);
                }

                var parsed = JsonSerializer.Deserialize(trimmedJson, entry.ArgsType, entry.JsonOptions);
                return !string.IsNullOrWhiteSpace(input)
                    ? MergeFallbackInput(parsed, input.Trim(), entry)
                    : parsed;
            }

            var scalar = ExtractScalarString(trimmedJson, entry.JsonOptions);
            if (!string.IsNullOrWhiteSpace(scalar))
            {
                return CoercePlainInput(scalar, entry);
            }

            if (!string.IsNullOrWhiteSpace(input))
            {
                return CoercePlainInput(input.Trim(), entry);
            }

            return JsonSerializer.Deserialize("{}", entry.ArgsType, entry.JsonOptions);
        }

        if (!string.IsNullOrWhiteSpace(input))
        {
            return CoercePlainInput(input.Trim(), entry);
        }

        return JsonSerializer.Deserialize("{}", entry.ArgsType, entry.JsonOptions);
    }

    private static string ExtractScalarString(string trimmed, JsonSerializerOptions options)
    {
        if (string.Equals(trimmed, "null", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed, options) ?? string.Empty;
            }
            catch (JsonException)
            {
                return trimmed.Trim('"');
            }
        }

        return trimmed;
    }

    private static object? CoercePlainInput(string rawInput, TargetEntry entry)
    {
        var clean = rawInput.Trim();
        if (clean.Length == 0)
        {
            return JsonSerializer.Deserialize("{}", entry.ArgsType, entry.JsonOptions);
        }

        if (entry.ArgsType == typeof(BrowserNavArgs))
        {
            if ((clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                && Uri.TryCreate(clean, UriKind.Absolute, out var uri)
                && string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
                && string.IsNullOrEmpty(uri.Fragment))
            {
                return new BrowserNavArgs(BaseUrl: clean);
            }

            return new BrowserNavArgs(Route: clean);
        }

        if (entry.ArgsType == typeof(StudioNavArgs))
        {
            if (IsCandidateToken(clean))
            {
                return new StudioNavArgs(CandidateId: clean);
            }

            if (IsStudioLensKeyword(clean))
            {
                return new StudioNavArgs(Lens: clean, Mode: clean);
            }

            if (IsExplicitStudioTarget(clean))
            {
                return new StudioNavArgs(TargetId: clean);
            }

            return new StudioNavArgs(TargetId: clean, FlowId: clean);
        }

        if (entry.ArgsType == typeof(TasksNavArgs))
        {
            var lower = clean.ToLowerInvariant();
            if (lower is "board" or "kanban" or "table" or "list" or "approvals" or "confirmations")
            {
                var tab = lower switch
                {
                    "kanban" => "board",
                    "list" => "table",
                    "confirmations" => "approvals",
                    _ => lower,
                };
                return new TasksNavArgs(Tab: tab);
            }

            if (clean.StartsWith("confirm:", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("confirmation:", StringComparison.OrdinalIgnoreCase))
            {
                var id = clean[(clean.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                return new TasksNavArgs(Tab: "approvals", ConfirmationId: id.Length > 0 ? id : null);
            }

            if (clean.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
            {
                var id = clean["draft:".Length..].Trim();
                return new TasksNavArgs(Tab: "approvals", DraftPreviewId: id.Length > 0 ? id : null);
            }

            return new TasksNavArgs(FlowId: clean);
        }

        if (entry.ArgsType == typeof(InoNavArgs))
        {
            if (clean.StartsWith("conversation:", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("chat:", StringComparison.OrdinalIgnoreCase))
            {
                var id = clean[(clean.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                return new InoNavArgs(ConversationId: id.Length > 0 ? id : null);
            }

            if (clean.StartsWith("focus:", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
            {
                return new InoNavArgs(FocusTarget: clean);
            }

            return new InoNavArgs(Prompt: clean);
        }

        if (entry.ArgsType == typeof(SettingsNavArgs))
        {
            if (clean.Contains('/', StringComparison.Ordinal))
            {
                var parts = clean.Split('/', 2, StringSplitOptions.TrimEntries);
                var section = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null;
                var subPage = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                return new SettingsNavArgs(Section: section, SubPage: subPage);
            }

            return new SettingsNavArgs(Section: clean);
        }

        var ctors = entry.ArgsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length > 0)
        {
            var parameters = ctors[0].GetParameters();
            if (parameters.Length > 0
                && parameters[0].ParameterType == typeof(string)
                && parameters.Skip(1).All(p => p.HasDefaultValue))
            {
                var ctorArgs = new object?[parameters.Length];
                ctorArgs[0] = clean;
                for (var i = 1; i < parameters.Length; i++)
                {
                    ctorArgs[i] = parameters[i].DefaultValue;
                }

                return ctors[0].Invoke(ctorArgs);
            }
        }

        return JsonSerializer.Deserialize("{}", entry.ArgsType, entry.JsonOptions);
    }

    private static object? MergeFallbackInput(object? parsed, string cleanInput, TargetEntry entry)
    {
        return parsed switch
        {
            BrowserNavArgs { Section: null, TraceId: null, GrainFilter: null, Route: null, BaseUrl: null } => CoercePlainInput(cleanInput, entry),
            StudioNavArgs { TargetId: null, Lens: null, FlowId: null, CandidateId: null, FilePath: null, Mode: null } => CoercePlainInput(cleanInput, entry),
            TasksNavArgs { FlowId: null, Tab: null, ConfirmationId: null, DraftPreviewId: null } => CoercePlainInput(cleanInput, entry),
            InoNavArgs { Prompt: null, ConversationId: null, FocusTarget: null } => CoercePlainInput(cleanInput, entry),
            SettingsNavArgs { Section: null, SubPage: null, Sheet: null, SearchQuery: null } => CoercePlainInput(cleanInput, entry),
            _ => parsed,
        };
    }

    private static bool IsCandidateToken(string candidate)
    {
        if (candidate.Length is < 2 or > 5 || candidate[0] is not ('w' or 'W'))
        {
            return false;
        }

        for (var i = 1; i < candidate.Length; i++)
        {
            if (!char.IsAsciiDigit(candidate[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStudioLensKeyword(string candidate)
        => candidate.ToLowerInvariant() is "flow" or "plan" or "code" or "changes" or "verify" or "pr" or "brain" or "state" or "source" or "signals";

    private static bool IsExplicitStudioTarget(string candidate)
        => candidate.StartsWith("trace/", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("trace:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("node:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("unit:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("pulse:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("edge:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("app.", StringComparison.OrdinalIgnoreCase);

    public sealed record TargetEntry(Type ArgsType, Type ArrivalType, MethodInfo Method, JsonSerializerOptions JsonOptions);
}
