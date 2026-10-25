using Orleans.Metadata;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

// Same grain as the brain runtime. Callers reach it through IWorkContinuation, not the script edge.
[Alias("microsoft.csharp.runtime-continuations"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeContinuations : IGrainWithStringKey
{
    Task Acquire(string token);

    Task Release(string token);
}
