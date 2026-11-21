using System.Net;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Modules.Circles.Signals;
using IntoChat.OS.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace IntoChat.OS.Modules.Circles.Tests.Unit;

public sealed class LegacyCirclesFacts
{
    [Fact]
    public void LegacyCircleRecordsStillRead()
    {
        var circle = JsonSerializer.Deserialize(
            """{"name":"team","owner":"ada","members":["ada","ben"],"sharedApps":["notes"],"computeSharePercent":10,"invited":["eve"],"version":4,"bindings":[{"accountId":"ben","appName":"notes","slot":"tables","connectionKey":"neuron://ben/conn/x"}],"admins":["ben"]}""",
            CirclesJson.Default.CircleRecord)!;
        Assert.Equal(["ada", "ben"], circle.Members);
        Assert.Equal(4L, circle.Version);
        Assert.Equal("tables", circle.Bindings.Single().Slot);

        var chat = JsonSerializer.Deserialize(
            """{"lines":[{"speaker":"ada","text":"hi","sequence":1}],"last":1,"said":["ada/r1"],"published":[]}""",
            CirclesJson.Default.CircleChatRecord)!;
        Assert.Equal("hi", chat.Lines.Single().Text);

        var invites = JsonSerializer.Deserialize(
            """{"invites":[{"id":"i","codeHash":"h","role":"member","issuedBy":"ada","machineId":"m1","machineKeyThumbprint":"t","acceptedAt":"2026-01-01T00:00:00+00:00"}]}""",
            CirclesJson.Default.CircleInvitesRecord);
        Assert.NotNull(invites);
        Assert.Equal("m1", invites!.Invites.Single().MachineId);

        var factory = JsonSerializer.Deserialize(
            """{"created":[{"brainId":"c1","name":"team"}]}""",
            CirclesJson.Default.CircleFactoryRecord)!;
        Assert.Equal("c1", factory.Created.Single().BrainId);
    }

    [Fact]
    public async Task AMemberReadsAnOldCircle()
    {
        await using var os = TestOS.Local();
        os.Register<LegacyCircleNeuron>();
        os.Register<LegacyCircleChatNeuron>();
        os.Register<LegacyCircleIndexNeuron>();
        os.Register<LegacyCircleMachinesNeuron>();

        var circleId = new NeuronId(new BrainId("circle-1"), "circles.circle", "circle");
        var chatId = new NeuronId(new BrainId("circle-1"), "circles.chat", "chat");
        os.SeedStoredState(circleId, """{"name":"team","owner":"ada","members":["ada","ben"],"sharedApps":[],"computeSharePercent":0,"invited":[],"version":2,"bindings":[],"admins":[]}""");
        os.SeedStoredState(chatId, """{"lines":[{"speaker":"ada","text":"hi","sequence":1}],"last":1,"said":[],"published":[]}""");

        using (TestEdge.Authority.Enter(TestEdge.Authority.Person("ben", "ben", new BrainId("circle-1"))))
        {
            var state = await os.Get<ILegacyCircle>(circleId).State();
            Assert.Equal(["ada", "ben"], state.Members);

            var lines = await os.Get<ILegacyCircleChat>(chatId).Read(0);
            Assert.Equal("hi", Assert.Single(lines).Text);
        }

        using (TestEdge.Authority.Enter(TestEdge.Authority.Person("eve", "eve", new BrainId("circle-1"))))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<ILegacyCircle>(circleId).State());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<ILegacyCircleChat>(chatId).Read(0));
        }
    }

    [Fact]
    public void OldCircleSignalsStillDecodeByAlias()
    {
        foreach (var (alias, type) in new[]
        {
            ("circles.circle-changed", typeof(CircleChanged)),
            ("circles.circle-spoke", typeof(CircleSpoke)),
            ("circles.membership-revoked", typeof(CircleMembershipRevoked)),
            ("circleroom.join", typeof(CircleRoomJoined)),
            ("circleroom.leave", typeof(CircleRoomLeft)),
        })
        {
            Assert.Equal(alias, type.GetCustomAttribute<SignalAttribute>()!.Alias);
        }
    }

    [Fact]
    public void NothingReStampsIntoAnotherBrain()
    {
        var srcDir = Path.Combine(FindRepoRoot(), "src");
        var offenders = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("BrainActs.", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task TheOldInvitationRoutesAnswerGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        new CirclesModule().Configure(app);
        await app.StartAsync(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            using var accept = await client.PostAsync("/circle-invitations/circle-1/accept", content: null, ct);
            Assert.Equal(HttpStatusCode.Gone, accept.StatusCode);
            Assert.Equal(CirclesModule.GoneMessage, await accept.Content.ReadAsStringAsync(ct));

            using var codes = await client.PostAsync("/circle-invitations/circle-1/codes", content: null, ct);
            Assert.Equal(HttpStatusCode.Gone, codes.StatusCode);
            Assert.Equal(CirclesModule.GoneMessage, await codes.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OWED.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
