using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

// The Ownership keys deployment writes (os#210 IntoChatOSNames.BrainStorageOwnership*) bind into BrainStoreOptions.
// Once those constants land in a shared assembly, this fact should reference them instead of the literals below.
public sealed class BrainStoreOwnershipOptionsFacts
{
    private const string KindKey = IntoChatOSNames.BrainStorageConfigurationKey + ":Ownership:Kind";
    private const string ContainerUriKey = IntoChatOSNames.BrainStorageConfigurationKey + ":Ownership:ContainerUri";

    [Theory]
    [InlineData("BlobLease", BrainLogOwnershipKind.BlobLease)]
    [InlineData("WriterLock", BrainLogOwnershipKind.WriterLock)]
    public void DeploymentKeysBindAndRoundTrip(string kind, BrainLogOwnershipKind expected)
    {
        const string uri = "https://account.blob.core.windows.net/brainlease-v1";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KindKey] = kind,
            [ContainerUriKey] = uri,
        }).Build();

        var ownership = configuration.GetSection(BrainStoreOptions.Key).Get<BrainStoreOptions>()!.Ownership;

        Assert.Equal(expected, ownership.Kind);
        Assert.Equal(new Uri(uri), ownership.ContainerUri);
        Assert.Equal(kind, ownership.Kind.ToString());
    }

    [Fact]
    public void AbsentKindIsTheWriterLock() =>
        Assert.Equal(BrainLogOwnershipKind.WriterLock, new ConfigurationBuilder().Build().GetSection(BrainStoreOptions.Key).Get<BrainStoreOptions>()?.Ownership.Kind
            ?? new BrainStoreOptions().Ownership.Kind);
}
