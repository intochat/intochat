namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class LegacyContainerFacts
{
    private sealed class FakeDocker(params string[] containers) : IDockerCli
    {
        public List<string> Containers { get; } = [.. containers];
        public List<string> Volumes { get; } = ["Modules-ollama-data", "intochat.os.apphost-5ef44d53ff-Modules-ollama-data"];
        public List<string> Removed { get; } = [];
        public bool Unreachable { get; init; }
        public Task<IReadOnlyList<string>> ContainerNamesAsync(CancellationToken cancellationToken)
            => Unreachable ? throw new InvalidOperationException("docker is not reachable") : Task.FromResult<IReadOnlyList<string>>(Containers);
        public Task<IReadOnlyList<string>> VolumeNamesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Volumes);
        public Task RemoveContainerAsync(string name, CancellationToken cancellationToken) { Removed.Add("container:" + name); return Task.CompletedTask; }
        public Task RemoveVolumeAsync(string name, CancellationToken cancellationToken) { Removed.Add("volume:" + name); return Task.CompletedTask; }
    }

    [Fact]
    public void TheOldNamesAreTheOnesTheContainersHadBeforeTheRename()
        => Assert.Equal(
            ["Modules-ollama-5ef44d53", "Modules-ollama-openwebui-5ef44d53", "Modules-searxng-5ef44d53"],
            LegacyContainers.Names("5ef44d53"));

    [Fact]
    public async Task OldNamedContainersAreRemovedAndVolumesAreNot()
    {
        var docker = new FakeDocker(
            "Modules-ollama-5ef44d53", "Modules-ollama-openwebui-5ef44d53", "Modules-searxng-5ef44d53", "unrelated",
            "ai-ollama-5ef44d53", "Modules-ollama-aaaaaaaa");

        await LegacyContainers.RemoveAsync(docker, "5ef44d53", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["container:Modules-ollama-5ef44d53", "container:Modules-ollama-openwebui-5ef44d53", "container:Modules-searxng-5ef44d53"],
            docker.Removed);
    }

    [Fact]
    public async Task NothingIsRemovedWhenNoneExist()
    {
        var docker = new FakeDocker("unrelated", "ai-ollama-5ef44d53");
        await LegacyContainers.RemoveAsync(docker, "5ef44d53", TestContext.Current.CancellationToken);
        Assert.Empty(docker.Removed);
    }

    [Fact]
    public async Task NothingIsRemovedWithoutAHash()
    {
        var docker = new FakeDocker("Modules-ollama-");
        await LegacyContainers.RemoveAsync(docker, "", TestContext.Current.CancellationToken);
        Assert.Empty(docker.Removed);
    }

    [Fact]
    public async Task ADockerFailureIsSwallowedByTheStartHook()
    {
        var docker = new FakeDocker("Modules-ollama-5ef44d53") { Unreachable = true };
        await LegacyContainers.TryRemoveAsync(docker, "5ef44d53", TestContext.Current.CancellationToken);
        Assert.Empty(docker.Removed);
    }

    [Fact]
    public void TheHashPrefixComesFromTheAppHostPathHash()
    {
        Assert.Equal("5ef44d53", LegacyContainers.Sha8("5EF44D53FF0123456789"));
        Assert.Equal("", LegacyContainers.Sha8(null));
        Assert.Equal("", LegacyContainers.Sha8("abc"));
    }
}
