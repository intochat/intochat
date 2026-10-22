using IntoChat.OS.Sdk.Http.Compute;

namespace IntoChat.OS.Framework.Compute;

public sealed class ExternalCallContextAccessor : IExternalCallContextAccessor
{
    public string RequireTraceId() => TraceContext.Current?.TraceId
        ?? throw new InvalidOperationException("External calls require an active compute trace.");
}
