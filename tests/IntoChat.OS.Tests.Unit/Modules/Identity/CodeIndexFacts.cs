using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Identity.Codes;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Identity;

public sealed class CodeIndexFacts
{
    [Fact]
    public async Task AnOldInviteCodeRecordLoadsAsLegacyAndResolvesToNothing()
    {
        const string legacyJson = """{"circleBrainId":"circle-1","inviteId":"inv-1","expiresAt":"2099-01-01T00:00:00+00:00"}""";
        var record = JsonSerializer.Deserialize(legacyJson, CodesJson.Default.CodeIndexRecord)!;
        Assert.Equal(("circle-1", "inv-1", CodePurpose.Legacy), (record.Brain, record.Reference, record.Purpose));

        await using var os = TestOS.Local();
        os.Register<CodeIndexNeuron>();
        os.Register<CodeKeyNeuron>();
        var id = new NeuronId(new BrainId("system"), "circles.invite-code", "legacy-h1");
        os.SeedStoredState(id, legacyJson);

        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system"));
        var index = os.Get<ICodeIndex>(id);
        Assert.Null(await index.Resolve(CodePurpose.Device));
        Assert.Null(await index.Resolve(CodePurpose.Friend));
    }

    [Fact]
    public async Task ABoundCodeResolvesOnceForItsPurposeOnly()
    {
        await using var os = TestOS.Local();
        os.Register<CodeIndexNeuron>();
        os.Register<CodeKeyNeuron>();

        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system"));
        var index = os.Get<ICodeIndex>(new NeuronId(new BrainId("system"), "circles.invite-code", "h1"));
        await index.Bind(CodePurpose.Friend, "ada", "", os.Clock.GetUtcNow() + TimeSpan.FromHours(1));
        Assert.Null(await index.Resolve(CodePurpose.Device));
        Assert.Equal("ada", (await index.Resolve(CodePurpose.Friend))!.Brain.Value);

        await index.Spend();
        Assert.Null(await index.Resolve(CodePurpose.Friend));
    }

    [Fact]
    public async Task AnExpiredBoundCodeResolvesToNothing()
    {
        await using var os = TestOS.Local();
        os.Register<CodeIndexNeuron>();
        os.Register<CodeKeyNeuron>();

        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system"));
        var index = os.Get<ICodeIndex>(new NeuronId(new BrainId("system"), "circles.invite-code", "h-exp"));
        await index.Bind(CodePurpose.Device, "ada", "ref-1", os.Clock.GetUtcNow() + TimeSpan.FromHours(1));
        Assert.NotNull(await index.Resolve(CodePurpose.Device));

        os.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(await index.Resolve(CodePurpose.Device));
    }

    [Fact]
    public void TheMovedNeuronsKeepTheirTypesAndKeys()
    {
        Assert.Equal("circles.invite-code", typeof(CodeIndexNeuron).GetCustomAttribute<NeuronAttribute>()!.Type);
        Assert.Equal("circles.invite-key", typeof(CodeKeyNeuron).GetCustomAttribute<NeuronAttribute>()!.Type);
        Assert.Equal(("system/main", "system/abc"), (ICodeKey.Key, ICodeIndex.Key("abc")));
        Assert.Equal("IntoChatOS:Circles:InviteHmacKey", ICodeKey.Configuration);
    }

    [Fact]
    public async Task CodeIndexAndKeyNeuronsRefuseCollisionsAndShortKeysWithRules()
    {
        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system"));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ICodeKey.Configuration] = Convert.ToBase64String(new byte[16]),
            })
            .Build());
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<CodeIndexNeuron>()
            .Register<CodeKeyNeuron>();

        var code = os.Get<ICodeIndex>(new NeuronId(new BrainId("system"), ICodeIndex.Type, "hash-1"));
        await code.Bind(CodePurpose.Device, "c1", "inv-1", os.Clock.GetUtcNow().AddHours(1));
        var collision = await Assert.ThrowsAsync<RuleViolationException>(() => code.Bind(CodePurpose.Device, "c1", "inv-2", os.Clock.GetUtcNow().AddHours(1)));
        Assert.Contains("circles.invite-code-available", collision.Rejected.Rules);

        var key = os.Get<ICodeKey>(new NeuronId(new BrainId("system"), ICodeKey.Type, "main"));
        var shortKey = await Assert.ThrowsAsync<RuleViolationException>(key.Get);
        Assert.Contains("circles.invite-hmac-key-length", shortKey.Rejected.Rules);
    }
}
