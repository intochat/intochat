using System.Text.Json;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Kernel;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Identity.Local.Tests.Unit;

public sealed class DirectoryStateMigrationFacts
{
    private const string LegacyDirectoryJson = """
        {
          "accounts": [
            {
              "accountId": "acct-alice",
              "name": "Alice",
              "ownerPrincipalId": "alice",
              "createdAt": "2026-01-01T00:00:00+00:00"
            },
            {
              "accountId": "acct-bob",
              "name": "Bob",
              "ownerPrincipalId": "bob",
              "createdAt": "2026-01-02T00:00:00+00:00"
            }
          ],
          "principals": [
            {
              "principalId": "alice",
              "defaultMembership": {
                "principalId": "alice",
                "accountId": "acct-alice",
                "brainId": "brain-alice",
                "role": 0,
                "displayName": "Alice",
                "joinedAt": "2026-01-01T00:00:00+00:00"
              },
              "registrationComplete": true,
              "brains": [
                { "id": "brain-alice", "kind": 0 },
                { "id": "circle-team", "kind": 1 }
              ]
            },
            {
              "principalId": "bob",
              "defaultMembership": {
                "principalId": "bob",
                "accountId": "acct-bob",
                "brainId": "brain-bob",
                "role": 0,
                "displayName": "Bob",
                "joinedAt": "2026-01-02T00:00:00+00:00"
              },
              "registrationComplete": true,
              "brains": [
                { "id": "brain-bob", "kind": 0 },
                { "id": "circle-team", "kind": 1 }
              ]
            }
          ],
          "brains": [
            {
              "brainId": "brain-alice",
              "accountId": "acct-alice",
              "ownerProvisioned": true,
              "kind": 0,
              "members": [
                {
                  "principalId": "alice",
                  "accountId": "acct-alice",
                  "brainId": "brain-alice",
                  "role": 0,
                  "displayName": "Alice",
                  "joinedAt": "2026-01-01T00:00:00+00:00"
                }
              ],
              "grants": [
                {
                  "id": "grant-notes-1",
                  "appId": "brain-alice/apps/notes",
                  "semanticTypeId": "os.notes",
                  "mode": 0,
                  "workspaceId": "brain-alice",
                  "owner": "brain:brain-alice",
                  "grantee": "app:brain-alice/apps/notes",
                  "target": "brain-alice/apps/notes",
                  "scope": "os.notes",
                  "grantedAt": "2026-01-01T01:00:00+00:00",
                  "revoked": false
                }
              ]
            },
            {
              "brainId": "circle-team",
              "accountId": "acct-alice",
              "ownerProvisioned": true,
              "kind": 1,
              "members": [
                {
                  "principalId": "alice",
                  "accountId": "acct-alice",
                  "brainId": "circle-team",
                  "role": 0,
                  "displayName": "Alice",
                  "joinedAt": "2026-01-03T00:00:00+00:00"
                },
                {
                  "principalId": "bob",
                  "accountId": "acct-bob",
                  "brainId": "circle-team",
                  "role": 1,
                  "displayName": "Bob",
                  "joinedAt": "2026-01-04T00:00:00+00:00"
                }
              ],
              "grants": []
            }
          ],
          "operations": [
            {
              "accountId": "acct-alice",
              "operationId": "op-circle-1",
              "member": {
                "principalId": "alice",
                "accountId": "acct-alice",
                "brainId": "circle-team",
                "role": 0,
                "displayName": "Alice",
                "joinedAt": "2026-01-03T00:00:00+00:00"
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task LegacyIdentityDirectoryState_MigratesOnActivation_PreservingAllDataAndIdempotency()
    {
        var observer = new RecordingObserver();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDirectoryChangeObserver>(observer);

        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<DirectoryNeuron>();

        var directoryId = new NeuronId(BrainId.Kernel, IDirectory.Type, "main");
        os.SeedStoredState(directoryId, LegacyDirectoryJson);

        var directory = os.Get<IDirectory>(directoryId);
        var ct = TestContext.Current.CancellationToken;

        using (os.Edge.Enter(os.Edge.Kernel(BrainId.Kernel)))
        {
            // Accounts preserved
            var aliceAccount = await directory.ReadAccount("acct-alice");
            Assert.NotNull(aliceAccount);
            Assert.Equal("alice", aliceAccount!.OwnerPrincipalId);
            Assert.Equal("Alice", aliceAccount.Name);

            var bobAccount = await directory.ReadAccount("acct-bob");
            Assert.NotNull(bobAccount);
            Assert.Equal("bob", bobAccount!.OwnerPrincipalId);

            // Principals preserved
            Assert.True(await directory.IsRegistered("alice"));
            Assert.True(await directory.IsRegistered("bob"));
            var aliceCompleted = await directory.CompleteRegistration("alice");
            Assert.NotNull(aliceCompleted);
            Assert.Equal("brain-alice", aliceCompleted!.Member.BrainId);

            // Brains and kinds preserved
            Assert.True(await directory.BrainExists("brain-alice"));
            Assert.True(await directory.BrainExists("circle-team"));
            Assert.Equal(BrainKind.Personal, await directory.KindOf("brain-alice"));
            Assert.Equal(BrainKind.Circle, await directory.KindOf("circle-team"));

            // Memberships and roles preserved
            var aliceBrains = await directory.BrainsOf("alice");
            Assert.Equal(2, aliceBrains.Length);
            Assert.Contains(aliceBrains, b => b.Id == "brain-alice" && b.Kind == BrainKind.Personal);
            Assert.Contains(aliceBrains, b => b.Id == "circle-team" && b.Kind == BrainKind.Circle);

            var bobInCircle = await directory.Membership("circle-team", "bob");
            Assert.NotNull(bobInCircle);
            Assert.Equal(MemberRole.Member, bobInCircle!.Role);
            Assert.Equal("acct-bob", bobInCircle.AccountId);

            // Grants preserved
            var grants = await directory.ListGrants("brain-alice");
            var grant = Assert.Single(grants);
            Assert.Equal("grant-notes-1", grant.Id);
            Assert.Equal("os.notes", grant.Scope);
            Assert.Equal("app:brain-alice/apps/notes", grant.Grantee);

            // Operations preserved (idempotent CreateBrain replay)
            var replayed = await directory.CreateBrain("acct-alice", "alice", "op-circle-1", "Alice", BrainKind.Circle);
            Assert.False(replayed.NewBrain);
            Assert.Equal("circle-team", replayed.Member.BrainId);
        }

        // Observer was notified on activation
        Assert.NotEmpty(observer.States);

        // Verify persisted JSON no longer contains legacy nested brain fields
        var stateJson = await ((IBrainObserver)os).State(directoryId, ct);
        Assert.NotNull(stateJson);
        foreach (var brainElement in stateJson.Value.GetProperty("brains").EnumerateArray())
        {
            Assert.False(brainElement.TryGetProperty("members", out _), "Migrated BrainRow should not persist legacy nested members.");
            Assert.False(brainElement.TryGetProperty("grants", out _), "Migrated BrainRow should not persist legacy nested grants.");
            Assert.False(brainElement.TryGetProperty("ownerProvisioned", out _), "Migrated BrainRow should not persist legacy ownerProvisioned.");
        }

        // Verify idempotency: running TryMigrate on the migrated state is a strict no-op
        var migratedState = stateJson.Value.Deserialize<DirectoryState>(JsonSerializerOptions.Web)!;
        Assert.False(DirectoryStateMigration.TryMigrate(migratedState, out var secondPass));
        Assert.Same(migratedState, secondPass);
    }

    [Fact]
    public async Task LinkPrincipal_And_ClaimBrain_And_SpendOnce_WorkEndToEnd()
    {
        await using var os = TestOS.Local().Register<DirectoryNeuron>();
        var directory = os.Get<IDirectory>(new NeuronId(BrainId.Kernel, IDirectory.Type, "main"));

        using (os.Edge.Enter(os.Edge.Kernel(BrainId.Kernel)))
        {
            var registered = await directory.RegisterPrincipal("alice", "Alice");
            Assert.True(registered.NewBrain);

            // LinkPrincipal: first time Linked, second time AlreadyLinked
            Assert.Equal(PrincipalLinkStatus.Linked, await directory.LinkPrincipal("alice", "external:cloud:alice@example.com"));
            Assert.Equal(PrincipalLinkStatus.AlreadyLinked, await directory.LinkPrincipal("alice", "external:cloud:alice@example.com"));
            Assert.Equal(PrincipalLinkStatus.NoSuchPrincipal, await directory.LinkPrincipal("nobody", "external:cloud:nobody@example.com"));

            var linkedBrains = await directory.BrainsOf("external:cloud:alice@example.com");
            Assert.Contains(linkedBrains, b => b.Id == registered.Member.BrainId);

            // ClaimBrain: preserves exact brainScopeId and carries grants
            var carried = new[] { Grant.Tuple("brain:try-123", "app:try-123/apps/notes", "try-123/apps/notes", "run") };
            var claimed = await directory.ClaimBrain("try-123", "alice", "Alice", carried);
            Assert.True(claimed.NewBrain);
            Assert.Equal("try-123", claimed.Member.BrainId);
            Assert.Equal(registered.Member.AccountId, claimed.Member.AccountId);

            // Claiming by another principal fails
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.ClaimBrain("try-123", "bob", "Bob"));

            // SpendOnce: spends once, refuses second spend
            var onceGrant = new Grant
            {
                Id = "once-1",
                AppId = "try-123/apps/notes",
                SemanticTypeId = "os.notes",
                Mode = GrantMode.Once,
                WorkspaceId = "try-123",
                Owner = "brain:try-123",
                Grantee = "app:try-123/apps/notes",
                Target = "try-123/apps/notes",
                Scope = "os.notes",
            };
            await directory.GrantShipped("try-123", [onceGrant]);
            Assert.True(await directory.SpendOnce("try-123", ["once-1"]));
            Assert.False(await directory.SpendOnce("try-123", ["once-1"]));
        }
    }

    private sealed class RecordingObserver : IDirectoryChangeObserver
    {
        public List<DirectoryState> States { get; } = [];
        public void OnDirectoryChanged(DirectoryState state) => States.Add(state);
    }
}
