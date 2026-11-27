using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog.Flows;

public sealed class MissionCollaboratorResolverFacts
{
    [Fact]
    public async Task CollaboratorsAreFriendsAndNoReflectionIsUsed()
    {
        var ada = new BrainId("ada");
        var run = new EngineerRun("r1", null, "goal", null, 1, null, null, 0, null, null, circleBrainId: "circle-1");
        var ben = new Friend(new AccountId("ben"), new BrainId("ben"), "Ben", DateTimeOffset.UnixEpoch);
        var services = new ServiceCollection()
            .AddSingleton<INeuronProxies>(new FakeProxies(ben))
            .BuildServiceProvider();

        var collaborators = await MissionCollaboratorResolver.ResolveAsync(
            ada,
            run,
            FlowStatus.Running,
            "implement-tasks",
            "ada",
            services,
            TestContext.Current.CancellationToken);

        Assert.Contains(collaborators, c => c is { Id: "ben", Kind: "friend", DisplayName: "Ben" });
        var resolverPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Modules.Workspace"), "Flows", "MissionCollaboratorResolver.cs");
        Assert.DoesNotContain("GetAssemblies", File.ReadAllText(resolverPath), StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", File.ReadAllText(resolverPath), StringComparison.Ordinal);
    }

    private static string FindProjectDirectory(string projectName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
        {
            dir = dir.Parent;
        }
        var root = dir!.FullName;
        var slnx = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Intochat.OS.slnx"));
        var projectPath = slnx.Descendants("Project")
            .Select(element => (string?)element.Attribute("Path"))
            .First(path => path is not null && Path.GetFileNameWithoutExtension(path).Equals(projectName, StringComparison.Ordinal));
        return Path.GetDirectoryName(Path.Combine(root, projectPath!.Replace('/', Path.DirectorySeparatorChar)))!;
    }

    private sealed class FakeProxies(params Friend[] roster) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IFriends))
            {
                return (T)(object)new FakeFriends(roster);
            }
            throw new InvalidOperationException($"Not registered: {typeof(T).Name}");
        }
    }

    private sealed class FakeFriends(Friend[] roster) : IFriends
    {
        public Task<Code> Mint() => throw new NotSupportedException();
        public Task<Friend> Redeem(Code code) => throw new NotSupportedException();
        public Task Unfriend(AccountId friend) => throw new NotSupportedException();
        public Task<Friend[]> List() => Task.FromResult(roster);
        public Task<bool> IsFriend(AccountId account) => Task.FromResult(roster.Any(f => f.Account == account));
        public Task<Friend> Accepted(string codeHash, string name, DigitalBrain.Invocation.BrainPublicKeys? publicKeys = null) => throw new NotSupportedException();
        public Task Unfriended() => throw new NotSupportedException();
        public Task<GroupInvite[]> Groups() => Task.FromResult<GroupInvite[]>([]);
        public Task Invited(GroupInvite group) => throw new NotSupportedException();
        public Task<DigitalBrain.Invocation.BrainPublicKeys> PublicKeys(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
