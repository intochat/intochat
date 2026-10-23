using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Framework.Platform.Entries;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class BrainKeyReleaseFacts
{
    private sealed class Refusing : IBrainProviderKeys
    {
        public Task<string?> Release(string brainId, string providerId, CancellationToken cancellationToken = default)
            => throw new ArgumentException("refused");
    }

    [Fact]
    public async Task AnArgumentErrorFromReleaseMeansNoBrainKey()
    {
        var credentials = new RegistrationCredentials(null!, TimeProvider.System, new Refusing());

        Assert.Null(await credentials.BrainKey("personal", AiProvider.OpenAI, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EveryReleasableProviderIdHasAKeyedProvider()
        => Assert.Equal(BrainEntries.KeyedProviderIds.Order(StringComparer.Ordinal), AiIntegrations.KeyedProviders.Select(AiIntegrations.IdOf).Order(StringComparer.Ordinal));
}
