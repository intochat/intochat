using IntoChat.OS;
using Orleans.Metadata;

namespace IntoChat.OS.Microsoft.CSharp.Runtime;

// Rules are judged in the brain's session whether or not it has programs: this ensures it and returns once the host holds it.
[Alias("microsoft.csharp.runtime-session"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeSession : IGrainWithStringKey
{
    Task Hold(CancellationToken cancellationToken = default);
}
