using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Identity.Codes;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Devices.Tests.Unit;

public sealed class DeviceEnrollmentFacts
{
    private static readonly byte[] Spki = Encoding.UTF8.GetBytes("test-subject-public-key-info-bytes");

    [Fact]
    public async Task AMintedCodeEnrollsOneMachineOnce()
    {
        await using var harness = await Open(owner: "ada");

        Code code;
        using (harness.AsPerson("ada")) { code = await harness.Enrollment.Mint(); }

        var first = await harness.Redeemer.Redeem(code.Value, "m1", "thumb", "Ada's PC");
        Assert.Equal((EnrollmentResult.Accepted, "ada", "ada", "Ada's PC"), (first.Result, first.Brain, first.Account, first.DeviceName));
        using (harness.AsDevicesModule("ada")) { Assert.True(await harness.Enrollment.IsLive("m1", "thumb")); }

        var repeat = await harness.Redeemer.Redeem(code.Value, "m1", "thumb", "Ada's PC");
        Assert.Equal((EnrollmentResult.Repeat, "ada", "ada"), (repeat.Result, repeat.Brain, repeat.Account));

        var otherMachine = await harness.Redeemer.Redeem(code.Value, "m2", "other", "Eve");
        Assert.Equal(EnrollmentResult.Rejected, otherMachine.Result);

        // Past the 5-minute repeat window even the original machine is rejected.
        harness.Os.Clock.Advance(IDeviceEnrollment.RepeatWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(EnrollmentResult.Rejected, (await harness.Redeemer.Redeem(code.Value, "m1", "thumb", "Ada's PC")).Result);
    }

    [Fact]
    public async Task AnExpiredCodeAnswersExpired()
    {
        await using var harness = await Open(owner: "ada");

        Code code;
        using (harness.AsPerson("ada")) { code = await harness.Enrollment.Mint(); }

        harness.Os.Clock.Advance(TimeSpan.FromHours(25));
        var outcome = await harness.Redeemer.Redeem(code.Value, "m1", "thumb", "Ada's PC");
        Assert.Equal(EnrollmentResult.Expired, outcome.Result);
    }

    [Fact]
    public async Task AFriendCodeDoesNotEnrollAMachine()
    {
        await using var harness = await Open(owner: "ada");

        var friendCode = Code.New();
        await harness.Book.Bind(friendCode, CodePurpose.Friend, new BrainId("ada"), "", harness.Os.Clock.GetUtcNow() + Code.Validity);

        var outcome = await harness.Redeemer.Redeem(friendCode.Value, "m1", "thumb", "Ada's PC");
        Assert.Equal(EnrollmentResult.Rejected, outcome.Result);
    }

    [Fact]
    public async Task ARevokedDeviceIsNotLive()
    {
        await using var harness = await Open(owner: "ada");

        Code code;
        using (harness.AsPerson("ada")) { code = await harness.Enrollment.Mint(); }
        Assert.Equal(EnrollmentResult.Accepted, (await harness.Redeemer.Redeem(code.Value, "m1", "thumb", "Ada's PC")).Result);
        using (harness.AsDevicesModule("ada")) { Assert.True(await harness.Enrollment.IsLive("m1", "thumb")); }

        using (harness.AsPerson("ada"))
        {
            await harness.Enrollment.Revoke("m1");
            var listed = await harness.Enrollment.List();
            var device = Assert.Single(listed);
            Assert.Equal(("m1", "Ada's PC", "thumb", true), (device.MachineId, device.Name, device.KeyThumbprint, device.Revoked));
        }

        using (harness.AsDevicesModule("ada")) { Assert.False(await harness.Enrollment.IsLive("m1", "thumb")); }
    }

    [Fact]
    public async Task OnlyTheOwnerAsAPersonMints()
    {
        await using var harness = await Open(owner: "ada");

        using (harness.AsApp("ada"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(harness.Enrollment.Mint);
        }
        using (harness.AsAgent("ada"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(harness.Enrollment.Mint);
        }
        using (harness.AsPerson("eve", brain: "ada"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(harness.Enrollment.Mint);
        }
        using (harness.AsPerson("ada", brain: "ben"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(harness.Enrollment.Mint);
        }
    }

    [Fact]
    public void TheThumbprintIsTheOldOne()
        => Assert.Equal(InviteCode.Thumbprint(Spki), DeviceKeys.Thumbprint(Spki));

#pragma warning disable CS0618
    [Fact]
    public async Task TheCircleShimAnswersInTheOldShape()
    {
        await using var harness = await Open(owner: "ada");

        Code code;
        using (harness.AsPerson("ada")) { code = await harness.Enrollment.Mint(); }

        var old = await harness.Services.GetRequiredService<ICircleInviteRedeemer>().Redeem(code.Value, "m1", "thumb", "PC");
        Assert.Equal((RedeemResult.Accepted, "ada", "PC", "owner", "ada", false), (old.Result, old.CircleId, old.CircleName, old.Role, old.AccountId, old.PendingAccount));
    }
#pragma warning restore CS0618

    private static Task<Harness> Open(string owner)
    {
        TestOS? os = null;
        var services = new ServiceCollection();
        services.AddSingleton(TestEdge.Authority);
        services.AddSingleton<IIdentityClient>(new StaticOwnerIdentity(owner, owner));
        services.AddSingleton<ICodeBook>(sp => new CodeBook(
            sp,
            _ => os!.Get<ICodeKey>(new NeuronId(new BrainId("system"), ICodeKey.Type, "main")),
            (_, hash) => os!.Get<ICodeIndex>(new NeuronId(new BrainId("system"), ICodeIndex.Type, hash))));
        services.AddSingleton<IDeviceEnrollmentRedeemer>(sp => new DeviceEnrollmentRedeemer(
            sp,
            sp.GetRequiredService<ICodeBook>(),
            (_, brain) => os!.Get<IDeviceEnrollment>(new NeuronId(new BrainId(brain), "devices.enrollment", "device-enrollment"))));
#pragma warning disable CS0618
        services.AddSingleton<ICircleInviteRedeemer, CircleInviteRedeemer>();
#pragma warning restore CS0618
        var provider = services.BuildServiceProvider();

        os = TestOS.Local(o => o.Services = provider);
        os.Register<CodeKeyNeuron>();
        os.Register<CodeIndexNeuron>();
        os.Register<DeviceEnrollmentNeuron>();

        var enrollment = os.Get<IDeviceEnrollment>(new NeuronId(new BrainId(owner), "devices.enrollment", "device-enrollment"));
        return Task.FromResult(new Harness(os, provider, enrollment));
    }

    private sealed class Harness(TestOS os, ServiceProvider services, IDeviceEnrollment enrollment) : IAsyncDisposable
    {
        public TestOS Os => os;
        public IServiceProvider Services => services;
        public IDeviceEnrollment Enrollment => enrollment;
        public ICodeBook Book => services.GetRequiredService<ICodeBook>();
        public IDeviceEnrollmentRedeemer Redeemer => services.GetRequiredService<IDeviceEnrollmentRedeemer>();

        public IDisposable AsPerson(string account, string? brain = null)
            => TestEdge.Authority.Enter(TestEdge.Authority.Person(account, account, new BrainId(brain ?? account)));

        public IDisposable AsApp(string brain)
            => TestEdge.Authority.Enter(TestEdge.Authority.App($"{brain}/apps/test", new Actor.Person(brain, brain), new BrainId(brain)));

        public IDisposable AsAgent(string brain)
            => TestEdge.Authority.Enter(TestEdge.Authority.Agent(new Actor.Person(brain, brain), new BrainId(brain)));

        public IDisposable AsDevicesModule(string brain)
            => TestEdge.Authority.Enter(TestEdge.Authority.Module("devices", new BrainId(brain)));

        public async ValueTask DisposeAsync()
        {
            await os.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class StaticOwnerIdentity(string brainId, string ownerAccount) : IIdentityClient
    {
        public Task<Account?> OwnerOfBrain(string brain)
            => Task.FromResult<Account?>(string.Equals(brain, brainId, StringComparison.Ordinal)
                ? new Account { AccountId = ownerAccount, Name = ownerAccount, OwnerPrincipalId = ownerAccount }
                : null);

        public Task<Account?> ReadAccount(string accountId) => throw new NotSupportedException();
        public Task EnsureAccount(Account account) => throw new NotSupportedException();
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind) => throw new NotSupportedException();
        public Task<BrainProvision> EnsureBrain(Member owner) => throw new NotSupportedException();
        public Task<BrainProvision> ProvisionOwner(Member owner) => throw new NotSupportedException();
        public Task<bool> IsRegistered(string principalId) => throw new NotSupportedException();
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => throw new NotSupportedException();
        public Task<BrainProvision?> CompleteRegistration(string principalId) => throw new NotSupportedException();
        public Task<BrainEntry[]> BrainsOf(string principalId) => throw new NotSupportedException();
        public Task<BrainKind> KindOf(string brainScopeId) => throw new NotSupportedException();
        public Task<bool> BrainExists(string brainScopeId) => throw new NotSupportedException();
        public Task<Member?> Membership(string brainScopeId, string principalId) => throw new NotSupportedException();
        public Task<Member?> EnsureMember(Member member) => throw new NotSupportedException();
        public Task RemoveMember(string brainScopeId, string accountId) => throw new NotSupportedException();
        public Task RemovePrincipalMember(string brainScopeId, string principalId) => throw new NotSupportedException();
        public Task<CallDecision?> Authorize(Caller? caller, CallRequest request) => throw new NotSupportedException();
        public Task<Grant[]> ListGrants(string brainScopeId) => throw new NotSupportedException();
        public Task<Grant> GrantAsOwner(string brainId, Grant grant) => throw new NotSupportedException();
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => throw new NotSupportedException();
        public Task RevokeAsOwner(string brainId, string grantId) => throw new NotSupportedException();
        public Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode) => throw new NotSupportedException();
    }
}
