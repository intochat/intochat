using Microsoft.Extensions.Configuration;

namespace IntoChat.Cloud.Hosting.Tests.Unit;

public sealed class HostedStorageGuardFacts
{
    [Fact]
    public void LoadsOnlyUnderCloud()
    {
        Assert.True(HostingModule.LoadsUnder("cloud"));
        Assert.True(HostingModule.LoadsUnder(" Cloud "));
        Assert.False(HostingModule.LoadsUnder("local"));
        Assert.False(HostingModule.LoadsUnder("local+cloud"));
        Assert.False(HostingModule.LoadsUnder(null));
    }

    [Fact]
    public void ARuntimeIdAloneDoesNotThrow()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostedStorageGuard.RuntimeIdKey] = "runtime-1",
        }).Build();
        HostedStorageGuard.Ensure(configuration);
    }

    [Fact]
    public void AGrainStateStoreAloneDoesNotThrow()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostedStorageGuard.GrainStateKey] = "UseDevelopmentStorage=true",
        }).Build();
        HostedStorageGuard.Ensure(configuration);
    }

    [Fact]
    public void ARuntimeIdWithAGrainStateStoreThrowsTheMigrationMessage()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostedStorageGuard.RuntimeIdKey] = "runtime-1",
            [HostedStorageGuard.GrainStateKey] = "UseDevelopmentStorage=true",
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => HostedStorageGuard.Ensure(configuration));
        Assert.Equal(HostedStorageGuard.MigrationMessage, error.Message);
    }
}
