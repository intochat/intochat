using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;

namespace IntoChat.OS.Microsoft.CSharp.Runtime;

// Sign-in warms the brain's sandbox session, so behaviors are alive before the first window opens.
internal sealed class RuntimeSignIn(IIntoChatOSBrains brains) : ISignInHandler
{
    public Task SignedIn(Member member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        return brains.Open(BrainScope.Create(member.AccountId, member.BrainId).Id).Neuron<IBrainRuntime>(BrainRuntimeKeys.Name).Touch(cancellationToken);
    }
}
