using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Framework.Platform.Secrets;
using IntoChat.OS.Authority.Tests.Integration.Integrations;
using IntoChat.OS.Authority.Hosted;
using IntoChat.OS.Authority.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Microsoft.Orleans;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

[Trait("host", "own")]
public sealed class HostedPlatformStorageFacts
{
    [Fact]
    public void HostedJournalDoesNotSilentlyReplaceAnExplicitAzurePlatformStore()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[HostedRuntimeOptions.SectionName + ":RuntimeId"] = "runtime-one";
        foreach (var field in new[] { "Clustering", "GrainState", "Reminders" })
        { builder.Configuration[IntegrationSchema.Key("orleans", field)] = "UseDevelopmentStorage=true"; }
        builder.AddHostedAuthority();
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddIntoChatOSServer(server => server.AddModule<OrleansModule>()));
        Assert.Contains("explicit migration", error.Message);
    }

    [Fact]
    public async Task HostedAuthoritySurvivesAColdHostWithoutReusedMemoryStores()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("hosted-platform-").FullName;
        var custody = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        TestBrain Create() => TestBrain.Create().WithModule<FakeAiModule>()
            .WithConfiguration(IntoChatOSNames.MasterKeyConfigurationKey, custody)
            .WithConfiguration(BrainStoreOptions.Key + ":Directory", Path.Combine(directory, "store"))
            .WithConfiguration("IntoChatOS:Auth:Posture", "Secured")
            .WithConfiguration(HostedRuntimeOptions.SectionName + ":AccountId", "account-one")
            .WithConfiguration(HostedRuntimeOptions.SectionName + ":BrainId", "brain-one")
            .WithConfiguration(HostedRuntimeOptions.SectionName + ":RuntimeId", "runtime-one")
            .WithConfiguration(HostedRuntimeOptions.SectionName + ":StateDirectory", Path.Combine(directory, "hosting"))
            .WithConfiguration(HostedRuntimeOptions.SectionName + ":ControllerCertificateSha256", new string('A', 64))
            // After the server composed its storage: the hosted authority applies its journal storage at once.
            .ConfigureServices(services => services.AddHostedAuthority(new ConfigurationBuilder()
                .AddInMemoryCollection([new(HostedRuntimeOptions.SectionName + ":RuntimeId", "runtime-one")]).Build()));
        try
        {
            SecretRef? reference = null;
            string? reminderTag = null;
            var reminderGrain = GrainId.Create("hosted-test", "job-one");
            var caller = new CallerContext
            {
                PrincipalId = "platform",
                AccountId = "account-one",
                BrainId = "brain-one",
                Kind = CallerKind.Platform,
                StampedBy = TrustedEdge.Platform,
                AppId = "platform"
            };
            await using (var first = await Create().StartAsync(ct))
            {
                Assert.IsType<HostedPlatformGrainStorage>(first.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage));
                var identity = first.Services.GetRequiredService<IIdentityClient>();
                await identity.EnsureAccount(new Account { AccountId = "account-one", OwnerPrincipalId = "account-one", Name = "Persisted account" });
                await identity.EnsureBrain(new Member
                {
                    AccountId = "account-one",
                    PrincipalId = "account-one",
                    BrainId = "brain-one",
                    DisplayName = "Persisted brain",
                    Role = MemberRole.Owner
                });
                reference = await first.Platform.Secrets("account-one").Set(caller, "test-key", "Test", "vault-canary", ct);
                await first.Platform.Integration("openai").Configure(new ConfigureIntegration
                { Values = new Dictionary<string, string> { ["ApiKey"] = "registration-canary" } });
                var reminders = Assert.IsType<HostedReminderTable>(first.Services.GetRequiredService<IReminderTable>());
                reminderTag = await reminders.UpsertRow(new ReminderEntry
                {
                    GrainId = reminderGrain,
                    ReminderName = "scheduled-work",
                    StartAt = DateTime.UtcNow.AddHours(1),
                    Period = TimeSpan.FromHours(1)
                });
            }
            await using (var second = await Create().StartAsync(ct))
            {
                var identity = second.Services.GetRequiredService<IIdentityClient>();
                var owner = await identity.OwnerOfBrain("brain-one");
                Assert.NotNull(owner);
                Assert.Equal("account-one", owner.AccountId);
                Assert.Equal("account-one", owner.OwnerPrincipalId);
                Assert.True(await identity.BrainExists(BrainScope.Create("account-one", "brain-one").Id));
                Assert.Equal("vault-canary", await second.Platform.Secrets("account-one").Resolve(caller, reference!, ct));
                var integration = await second.Platform.Integration("openai").Release(caller);
                Assert.Equal("registration-canary", integration.Values["ApiKey"]);
                var reminders = Assert.IsType<HostedReminderTable>(second.Services.GetRequiredService<IReminderTable>());
                var restored = await reminders.ReadRow(reminderGrain, "scheduled-work");
                Assert.Equal(reminderTag, restored!.ETag);
                Assert.Single((await reminders.ReadRows(reminderGrain)).Reminders);
                var hash = reminderGrain.GetUniformHashCode();
                Assert.Single((await reminders.ReadRows(hash, hash)).Reminders);
                Assert.Empty((await reminders.ReadRows(hash, unchecked(hash + 1))).Reminders);
                var newTag = await reminders.UpsertRow(restored);
                Assert.False(await reminders.RemoveRow(reminderGrain, "scheduled-work", reminderTag!));
                Assert.True(await reminders.RemoveRow(reminderGrain, "scheduled-work", newTag!));
                Assert.Null(await reminders.ReadRow(reminderGrain, "scheduled-work"));
                Assert.True(await second.Services.GetRequiredService<BrainLogs>().CheckStorageAsync(BrainScope.Create("account-one", "brain-one").Id, ct));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
