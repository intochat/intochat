using IntoChat.OS.Sdk.Http.Compute;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Connections.Compute;

public sealed class ExternalCallContextAccessor : IExternalCallContextAccessor
{
    public string RequireTraceId() => TraceContext.Current?.TraceId
        ?? throw new InvalidOperationException("External calls require an active compute trace.");
}
