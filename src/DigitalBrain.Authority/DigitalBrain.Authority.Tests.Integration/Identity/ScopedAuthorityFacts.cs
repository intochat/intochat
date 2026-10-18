using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Authority.Identity.Authority;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Authority.Tests.Integration.Identity;

public sealed class ScopedAuthorityFacts(AuthorityHost host)
{
    [Fact]
    public async Task SignInFinishesLocalHostingAfterTrustRegistrationAlreadyCompleted()
    {
        var brain = host.Brain;
        var interruptedHostingId = AuthorityHost.Unique("interrupted-hosting");
        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        var passwords = brain.Services.GetRequiredService<PrincipalPasswords>();
        var principal = interruptedHostingId;
        const string password = "long-test-password";
        var hash = new global::Microsoft.AspNetCore.Identity.PasswordHasher<string>().HashPassword(principal, password);
        Assert.True(await brain.Grains.GetGrain<IPrincipalCredential>(principal).TryCreate(hash));
        var provision = await identity.RegisterPrincipal(principal, "Interrupted hosting");
        Assert.True(provision.NewBrain);
        Assert.True(await identity.IsRegistered(principal));

        Assert.Null(await passwords.Authenticate(principal, "wrong-password"));
        Assert.Equal(provision.Member, await passwords.Authenticate(principal, password));
        var created = brain.Grains.GetGrain<IBrain>(provision.Member.BrainId);
        var original = await created.Read();
        Assert.Equal(provision.Member.AccountId, original.Owner.AccountId);
        Assert.Equal(provision.Member, await passwords.Authenticate(principal, password));
        Assert.Equal(original, await created.Read());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RegistrationResumesAfterProvisioningFailsWithoutAllocatingAnotherAccount()
    {
        await using var brain = await TestBrain.Create().ConfigureServices(services =>
            services.AddKeyedSingleton<IGrainStorage>(DigitalBrainNames.PlatformGrainStorage,
                (services, _) => new FailOnceStorage(new OrleansGrainStorageSerializer(services.GetRequiredService<Serializer>()))))
            .StartAsync(TestContext.Current.CancellationToken);
        // Interrupt the registration under test, after the harness owner has bootstrapped.
        ((FailOnceStorage)brain.Services.GetRequiredKeyedService<IGrainStorage>(DigitalBrainNames.PlatformGrainStorage)).Arm();
        var principal = brain.Grains.GetGrain<IPrincipal>("recoverable");
        var passwords = brain.Services.GetRequiredService<PrincipalPasswords>();
        var failure = await Assert.ThrowsAsync<OrleansException>(() => passwords.Register("recoverable", "long-test-password", "Recoverable"));
        Assert.IsType<IOException>(failure.InnerException);
        var allocated = await principal.DefaultMembership();
        Assert.NotNull(allocated);
        Assert.Null(await passwords.Authenticate("recoverable", "wrong-password"));
        var resumed = await passwords.Register("recoverable", "long-test-password", "Recoverable");
        Assert.Equal(allocated, resumed);
        Assert.Equal(resumed, await passwords.Authenticate("recoverable", "long-test-password"));
        Assert.NotNull(await brain.Grains.GetGrain<IBrainAuthority>(BrainScope.Create(resumed.AccountId, resumed.BrainId).Id).Membership("recoverable"));
    }

    private sealed class FailOnceStorage(IGrainStorageSerializer serializer) : IGrainStorage
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, GrainId), BinaryData> _data = new();
        private int _armed;
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public Task ReadStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            if (_data.TryGetValue((name, id), out var bytes)) { state.State = serializer.Deserialize<T>(bytes); state.RecordExists = true; }
            return Task.CompletedTask;
        }
        public Task WriteStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            if (typeof(T) == typeof(AccountState) && Interlocked.Exchange(ref _armed, 0) == 1)
            { throw new IOException("Interrupted account provisioning."); }
            _data[(name, id)] = serializer.Serialize(state.State);
            state.RecordExists = true;
            return Task.CompletedTask;
        }
        public Task ClearStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        { _data.TryRemove((name, id), out _); return Task.CompletedTask; }
    }

    [Fact]
    public async Task RevokedOwnerCannotMutateGrantsWithAnOldSession()
    {
        var brain = host.Brain;
        var accountId = AuthorityHost.Unique("account");
        var aliceId = AuthorityHost.Unique("alice");
        var brainId = AuthorityHost.Unique("brain");
        var authority = brain.Grains.GetGrain<IBrainAuthority>(BrainScope.Create(accountId, brainId).Id);
        await authority.EnsureMember(new Member { PrincipalId = aliceId, AccountId = accountId, BrainId = brainId, DisplayName = "Alice", Role = MemberRole.Owner });
        var caller = new DigitalBrain.Framework.CallerContext
        {
            PrincipalId = aliceId,
            AccountId = accountId,
            BrainId = brainId,
            Kind = DigitalBrain.Framework.CallerKind.User,
            StampedBy = DigitalBrain.Framework.TrustedEdge.AuthenticatedHttp
        };
        var grant = new Grant { AppId = "app", WorkspaceId = brainId, SemanticTypeId = "person.email", Mode = GrantMode.Always };
        await authority.GrantAsOwner(caller, grant);
        await authority.RemoveMember(aliceId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.GrantAsOwner(caller, grant));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.RevokeAsOwner(caller, "app", "person.email", GrantMode.Always));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task BasicBootstrapOwnsOnlyItsExplicitDefaultScope()
    {
        await using var brain = await TestBrain.Create().ConfigureServices(services =>
            services.Configure<DigitalBrain.Authority.Identity.Configuration.AuthOptions>(options =>
            {
                options.Posture = DigitalBrain.Authority.Identity.Configuration.IdentityPosture.Secured;
                options.Username = "operator";
                options.Password = "configured-bootstrap-password";
            })).StartAsync(TestContext.Current.CancellationToken);
        var access = brain.Services.GetRequiredService<IBrainAccess>();
        Assert.True(await access.CanAccessAsync("operator", "operator", "owner", TestContext.Current.CancellationToken));
        Assert.False(await access.CanAccessAsync("operator", "operator", "another-brain", TestContext.Current.CancellationToken));
        Assert.False(await access.CanAccessAsync("operator", "another-account", "owner", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABrainIdBelongsToOneAccountAndAnotherCannotClaimOrReachIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var aliceId = AuthorityHost.Unique("alice");
        var firstId = AuthorityHost.Unique("first");
        var secondId = AuthorityHost.Unique("second");
        var sharedNameId = AuthorityHost.Unique("shared-name");
        var authority = brain.Grains.GetGrain<IBrainAuthority>(BrainScope.Create(firstId, sharedNameId).Id);
        await authority.EnsureMember(new Member { PrincipalId = aliceId, AccountId = firstId, BrainId = sharedNameId, DisplayName = "Alice", Role = MemberRole.Owner });

        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Grains.GetGrain<IBrainAuthority>(BrainScope.Create(secondId, sharedNameId).Id).Initialize(secondId, sharedNameId));
        var access = brain.Services.GetRequiredService<IBrainAccess>();
        Assert.True(await access.CanAccessAsync(aliceId, firstId, sharedNameId, ct));
        Assert.False(await access.CanAccessAsync(aliceId, secondId, sharedNameId, ct));
    }

    [Fact]
    public async Task RetryingBrainCreationKeepsItsIdAndDoesNotRestoreRevokedMembership()
    {
        var brain = host.Brain;
        var aliceId = AuthorityHost.Unique("alice");
        var member = await brain.Services.GetRequiredService<PrincipalPasswords>().Register(aliceId, "long-test-password", "Alice");
        var account = brain.Grains.GetGrain<IAccount>(member.AccountId);
        var first = (await account.CreateBrain(aliceId, "operation-1", "First", BrainKind.Personal)).Member;
        var authority = brain.Grains.GetGrain<IBrainAuthority>(BrainScope.Create(first.AccountId, first.BrainId).Id);
        await authority.RemoveMember(aliceId);
        Assert.Equal(first, (await account.CreateBrain(aliceId, "operation-1", "First", BrainKind.Personal)).Member);
        Assert.Null(await authority.Membership(aliceId));
        Assert.NotEqual(first.BrainId, (await account.CreateBrain(aliceId, "operation-2", "Second", BrainKind.Personal)).Member.BrainId);
    }

}
