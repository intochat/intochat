using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI.Agents;

public static class AgentFailures
{
    public static AgentFailure Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ClassifyCore(error, depth: 0);
    }

    public static string CodeOf(AgentFailure failure) => failure switch
    {
        AgentFailure.NoProvider => "no-provider",
        AgentFailure.ProviderRejectedKey => "provider-rejected-key",
        AgentFailure.ProviderUnavailable => "provider-unavailable",
        AgentFailure.ToolFailed => "tool-failed",
        AgentFailure.TimedOut => "timed-out",
        _ => "unknown",
    };

    public static AgentFailure ParseCode(string? code) => code switch
    {
        "no-provider" => AgentFailure.NoProvider,
        "provider-rejected-key" => AgentFailure.ProviderRejectedKey,
        "provider-unavailable" => AgentFailure.ProviderUnavailable,
        "tool-failed" => AgentFailure.ToolFailed,
        "timed-out" => AgentFailure.TimedOut,
        _ => AgentFailure.Unknown,
    };

    private static AgentFailure ClassifyCore(Exception error, int depth)
    {
        if (depth > 8)
        {
            return AgentFailure.Unknown;
        }

        switch (error)
        {
            case NoProviderConfiguredException:
                return AgentFailure.NoProvider;

            case ProviderUnavailableException unavailable
                when unavailable.Missing.Length > 0
                    || string.Equals(unavailable.Status, "Unconfigured", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(unavailable.Status, "Missing", StringComparison.OrdinalIgnoreCase):
                return AgentFailure.NoProvider;

            case ProviderUnavailableException:
            case IntoChat.OS.Modules.Devices.DeviceUnavailableException:
                return AgentFailure.ProviderUnavailable;

            case HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }:
            case ClientResultException { Status: 401 or 403 }:
                return AgentFailure.ProviderRejectedKey;

            case HttpRequestException:
            case SocketException:
            case ClientResultException { Status: >= 500 or 0 }:
                return AgentFailure.ProviderUnavailable;

            case TimeoutException:
                return AgentFailure.TimedOut;

            case TargetInvocationException target:
                if (target.InnerException is { } targetInner)
                {
                    var innerKind = ClassifyCore(targetInner, depth + 1);
                    return innerKind == AgentFailure.Unknown ? AgentFailure.ToolFailed : innerKind;
                }
                return AgentFailure.ToolFailed;

            case AggregateException aggregate:
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    var kind = ClassifyCore(inner, depth + 1);
                    if (kind != AgentFailure.Unknown)
                    {
                        return kind;
                    }
                }
                return AgentFailure.Unknown;
        }

        if (error.InnerException is { } next)
        {
            var innerClassified = ClassifyCore(next, depth + 1);
            if (innerClassified != AgentFailure.Unknown)
            {
                return innerClassified;
            }
        }

        if (error.Data.Contains("AgentToolFailed"))
        {
            return AgentFailure.ToolFailed;
        }

        return AgentFailure.Unknown;
    }
}
