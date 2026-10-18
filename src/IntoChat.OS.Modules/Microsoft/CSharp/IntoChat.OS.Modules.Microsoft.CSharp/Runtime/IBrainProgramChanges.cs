using Orleans.Metadata;

namespace IntoChat.OS.Microsoft.CSharp.Runtime;

// How an Assign, compiled and stored while the runtime went on, takes a turn of the runtime to change its state.
// Sent by the runtime to itself: the ticket names a program only that activation compiled, so no other caller
// has anything to apply.
[Alias("microsoft.csharp.runtime-program-changes"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainProgramChanges : IGrainWithStringKey
{
    Task<ProgramView> Apply(long ticket);
}
