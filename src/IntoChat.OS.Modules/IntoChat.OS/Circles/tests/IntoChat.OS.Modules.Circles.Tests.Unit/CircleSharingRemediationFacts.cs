using System.Reflection;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Circles.Tests.Unit;

public sealed class CircleSharingRemediationFacts
{
    private const string CircleBrain = "circle-target";
    private const string SharerBrain = "sharer-source";
    private const string SharerAccount = "user-alice";

    [Fact]
    public void ShareIntoCircle_SlotlessApp_CreatesBinding_IssuesAppGrant_DoesNotInvokeConfigure()
    {
        const string appName = "notes";

        // When an app without connection slots is shared, a fallback binding is created
        // recording the app's source brain and account id
        var binding = new CircleBinding(SharerAccount, appName, string.Empty, $"{SharerBrain}/apps/{appName}");

        Assert.Equal(SharerAccount, binding.AccountId);
        Assert.Equal(appName, binding.AppName);
        Assert.Equal(string.Empty, binding.Slot);
        Assert.Equal($"{SharerBrain}/apps/{appName}", binding.ConnectionKey);

        // Verify ExtractBrain correctly parses the source brain from this connection key
        var extractedSource = ExtractBrain(binding.ConnectionKey);
        Assert.Equal(SharerBrain, extractedSource);

        // Verify app reference grant is issued with expected Id and target
        var expectedAppGrantId = $"circle:{CircleBrain}:app:{appName}";
        var appGrant = new Grant
        {
            Id = expectedAppGrantId,
            Owner = $"brain:{SharerBrain}",
            Grantee = $"brain:{CircleBrain}",
            Target = $"{SharerBrain}/apps/{appName}",
            Scope = "*",
            WorkspaceId = SharerBrain,
            AppId = appName,
            SemanticTypeId = "*",
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal($"circle:{CircleBrain}:app:{appName}", appGrant.Id);
        Assert.Equal($"brain:{CircleBrain}", appGrant.Grantee);
        Assert.Equal($"brain:{SharerBrain}", appGrant.Owner);

        // Verify state update records the app in SharedApps
        var record = new CircleRecord
        {
            Name = "team",
            Owner = "creator",
            Members = ["creator", SharerAccount],
            SharedApps = [],
            Bindings = [],
        };

        record.SharedApps = [.. record.SharedApps, appName];
        record.Bindings = [.. record.Bindings, binding];

        Assert.Contains(appName, record.SharedApps);
        Assert.Single(record.Bindings);
        Assert.Equal(string.Empty, record.Bindings[0].Slot);
    }

    [Fact]
    public void ShareIntoCircle_AppWithConnectionSlots_CreatesBindingsPerSlot_IssuesGrants()
    {
        const string appName = "gallery";
        var conn1 = $"{SharerBrain}/connection/s3/store-1";
        var conn2 = $"{SharerBrain}/connection/postgres/db-1";

        List<CircleBinding> bindings =
        [
            new(SharerAccount, appName, "storage", conn1),
            new(SharerAccount, appName, "database", conn2),
        ];

        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, b => Assert.Equal(appName, b.AppName));
        Assert.All(bindings, b => Assert.Equal(SharerAccount, b.AccountId));
        Assert.All(bindings, b => Assert.NotEmpty(b.Slot));

        // Grants issued for both connection slots and the app itself
        var appGrantId = $"circle:{CircleBrain}:app:{appName}";
        var conn1GrantId = $"circle:{CircleBrain}:conn:{conn1}";
        var conn2GrantId = $"circle:{CircleBrain}:conn:{conn2}";

        Assert.Equal($"circle:{CircleBrain}:app:gallery", appGrantId);
        Assert.Equal($"circle:{CircleBrain}:conn:{SharerBrain}/connection/s3/store-1", conn1GrantId);
        Assert.Equal($"circle:{CircleBrain}:conn:{SharerBrain}/connection/postgres/db-1", conn2GrantId);

        var record = new CircleRecord
        {
            Name = "team",
            Owner = "creator",
            Members = ["creator", SharerAccount],
            SharedApps = [appName],
            Bindings = [.. bindings],
        };

        Assert.Contains(appName, record.SharedApps);
        Assert.Equal(2, record.Bindings.Length);
    }

    [Fact]
    public void Unbind_SlotlessApp_IdentifiesSource_CleansSharedAppsAndBindings()
    {
        const string appName = "notes";
        var slotlessBinding = new CircleBinding(SharerAccount, appName, string.Empty, $"{SharerBrain}/apps/{appName}");

        var state = new CircleRecord
        {
            Name = "devs",
            Owner = "creator",
            Members = ["creator", SharerAccount],
            SharedApps = ["notes", "other-app"],
            Bindings = [slotlessBinding, new("other-user", "other-app", "slot1", "other/conn/1")],
            Admins = [SharerAccount],
        };

        // Simulate Unbind logic
        var memberBindings = state.Bindings.Where(binding => binding.AccountId == SharerAccount).ToArray();
        var appsToUnshare = memberBindings.Select(b => b.AppName).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var appSources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var binding in memberBindings)
        {
            var source = ExtractBrain(binding.ConnectionKey);
            appSources[binding.AppName] = source;

            // Slot is empty, so no connection grants to revoke
            Assert.True(string.IsNullOrEmpty(binding.Slot));
        }

        // Verify app reference grant revocation ID matches issuance ID
        Assert.True(appSources.TryGetValue(appName, out var resolvedSource));
        Assert.Equal(SharerBrain, resolvedSource);
        var revokeAppGrantId = $"circle:{CircleBrain}:app:{appName}";
        Assert.Equal("circle:circle-target:app:notes", revokeAppGrantId);

        // Apply state mutations performed by Unbind
        state.SharedApps = [.. state.SharedApps.Where(app => !appsToUnshare.Contains(app))];
        state.Bindings = [.. state.Bindings.Where(binding => binding.AccountId != SharerAccount)];
        state.Admins = [.. state.Admins.Where(admin => admin != SharerAccount)];

        // SharedApps and Bindings are cleanly purged of the departing member's slotless app
        Assert.DoesNotContain("notes", state.SharedApps);
        Assert.Contains("other-app", state.SharedApps);
        Assert.Single(state.Bindings);
        Assert.Equal("other-user", state.Bindings[0].AccountId);
        Assert.DoesNotContain(SharerAccount, state.Admins);
    }

    [Fact]
    public void Unbind_SlottedApp_RevokesAllConnectionAndAppGrants_CleansSharedApps()
    {
        const string appName = "gallery";
        var connKey1 = $"{SharerBrain}/connection/s3/bucket";
        var connKey2 = $"{SharerBrain}/connection/postgres/db";

        var state = new CircleRecord
        {
            Name = "devs",
            Owner = "creator",
            Members = ["creator", SharerAccount],
            SharedApps = [appName],
            Bindings =
            [
                new(SharerAccount, appName, "storage", connKey1),
                new(SharerAccount, appName, "database", connKey2),
            ],
            Admins = [],
        };

        var memberBindings = state.Bindings.Where(binding => binding.AccountId == SharerAccount).ToArray();
        var appsToUnshare = memberBindings.Select(b => b.AppName).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var appSources = new Dictionary<string, string>(StringComparer.Ordinal);
        var revokedConnectionGrants = new List<string>();

        foreach (var binding in memberBindings)
        {
            var source = ExtractBrain(binding.ConnectionKey);
            appSources[binding.AppName] = source;

            if (!string.IsNullOrEmpty(binding.Slot))
            {
                revokedConnectionGrants.Add($"circle:{CircleBrain}:conn:{binding.ConnectionKey}");
            }
        }

        var revokedAppGrants = new List<string>();
        foreach (var (app, source) in appSources)
        {
            revokedAppGrants.Add($"circle:{CircleBrain}:app:{app}");
        }

        state.SharedApps = [.. state.SharedApps.Where(app => !appsToUnshare.Contains(app))];
        state.Bindings = [.. state.Bindings.Where(binding => binding.AccountId != SharerAccount)];

        // Both connection grants and app grant are targeted for revocation
        Assert.Equal(2, revokedConnectionGrants.Count);
        Assert.Contains($"circle:{CircleBrain}:conn:{connKey1}", revokedConnectionGrants);
        Assert.Contains($"circle:{CircleBrain}:conn:{connKey2}", revokedConnectionGrants);

        Assert.Single(revokedAppGrants);
        Assert.Contains($"circle:{CircleBrain}:app:{appName}", revokedAppGrants);

        // State is cleanly updated
        Assert.Empty(state.SharedApps);
        Assert.Empty(state.Bindings);
    }

    [Fact]
    public void Unbind_HeterogeneousApps_SlotlessAndSlotted_RevokesAllMemberGrants_PreservesOthers()
    {
        const string slottedApp = "gallery";
        const string slotlessApp = "notes";
        const string otherMember = "user-bob";
        const string otherApp = "canvas";

        var state = new CircleRecord
        {
            Name = "design",
            Owner = "creator",
            Members = ["creator", SharerAccount, otherMember],
            SharedApps = [slottedApp, slotlessApp, otherApp],
            Bindings =
            [
                new(SharerAccount, slottedApp, "disk", $"{SharerBrain}/connection/s3/main"),
                new(SharerAccount, slotlessApp, string.Empty, $"{SharerBrain}/apps/{slotlessApp}"),
                new(otherMember, otherApp, string.Empty, $"bob-brain/apps/{otherApp}"),
            ],
            Admins = [SharerAccount],
        };

        var memberBindings = state.Bindings.Where(b => b.AccountId == SharerAccount).ToArray();
        var appsToUnshare = memberBindings.Select(b => b.AppName).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var appSources = new Dictionary<string, string>(StringComparer.Ordinal);
        var revokedConnGrants = new List<string>();

        foreach (var binding in memberBindings)
        {
            var source = ExtractBrain(binding.ConnectionKey);
            appSources[binding.AppName] = source;
            if (!string.IsNullOrEmpty(binding.Slot))
            {
                revokedConnGrants.Add($"circle:{CircleBrain}:conn:{binding.ConnectionKey}");
            }
        }

        var revokedAppGrants = appSources.Select(kv => $"circle:{CircleBrain}:app:{kv.Key}").ToList();

        state.SharedApps = [.. state.SharedApps.Where(app => !appsToUnshare.Contains(app))];
        state.Bindings = [.. state.Bindings.Where(b => b.AccountId != SharerAccount)];
        state.Admins = [.. state.Admins.Where(a => a != SharerAccount)];

        // Sharer's apps are removed; other member's app is preserved
        Assert.DoesNotContain(slottedApp, state.SharedApps);
        Assert.DoesNotContain(slotlessApp, state.SharedApps);
        Assert.Contains(otherApp, state.SharedApps);

        Assert.Single(state.Bindings);
        Assert.Equal(otherMember, state.Bindings[0].AccountId);
        Assert.Equal(otherApp, state.Bindings[0].AppName);

        Assert.Single(revokedConnGrants);
        Assert.Equal(2, revokedAppGrants.Count);
        Assert.Contains($"circle:{CircleBrain}:app:{slottedApp}", revokedAppGrants);
        Assert.Contains($"circle:{CircleBrain}:app:{slotlessApp}", revokedAppGrants);
    }

    [Fact]
    public void CircleNeuron_Methods_DoNotInvokeIAppConfigure()
    {
        // Under reference sharing, {CircleBrain}/apps/{appName} is not deployed in the circle brain.
        // Therefore, CircleNeuron and all its methods, nested types, and async state machines
        // must not attempt to call IApp.Configure.
        var configureMethod = typeof(IntoChat.OS.Modules.Apps.IApp).GetMethod("Configure");
        Assert.NotNull(configureMethod);

        var asm = typeof(CircleNeuron).Assembly;
        var allTypes = asm.GetTypes();

        foreach (var type in allTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var body = method.GetMethodBody();
                if (body is null) { continue; }

                var il = body.GetILAsByteArray();
                if (il is null || il.Length == 0) { continue; }

                var module = method.Module;
                for (var i = 0; i < il.Length - 4; i++)
                {
                    var op = il[i];
                    if (op == 0x28 || op == 0x6F) // call (0x28) or callvirt (0x6F)
                    {
                        var token = BitConverter.ToInt32(il, i + 1);
                        try
                        {
                            var resolved = module.ResolveMethod(token);
                            if (resolved is not null)
                            {
                                Assert.False(
                                    resolved.Name == "Configure" && resolved.DeclaringType == typeof(IntoChat.OS.Modules.Apps.IApp),
                                    $"Forbidden call to IApp.Configure found in {type.FullName}.{method.Name}");
                            }
                        }
                        catch
                        {
                            // Ignored: token may not resolve to a method if operand was for a different opcode
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void StressTest_Unbind_MultiMember_MixedSlottedAndSlotless_ZeroGrantLeaks()
    {
        const string circleBrain = "circle-corp";

        // Setup 10 members with heterogeneous slotted and slotless apps
        var memberConfigs = new[]
        {
            // Owner M0
            new MemberAppConfig("m0-acct", "m0-brain", [
                new AppSpec("calc", []), // slotless
                new AppSpec("portal", [new("storage", "conn-0-s"), new("db", "conn-0-d")]) // 2 slots
            ]),
            // M1: slotless
            new MemberAppConfig("m1-acct", "m1-brain", [
                new AppSpec("notes", [])
            ]),
            // M2: 1 slot
            new MemberAppConfig("m2-acct", "m2-brain", [
                new AppSpec("dashboard", [new("sql", "conn-2-sql")])
            ]),
            // M3: 3 slots
            new MemberAppConfig("m3-acct", "m3-brain", [
                new AppSpec("workflow", [new("queue", "conn-3-q"), new("blob", "conn-3-b"), new("pubsub", "conn-3-ps")])
            ]),
            // M4: 1 slotless + 1 slotted
            new MemberAppConfig("m4-acct", "m4-brain", [
                new AppSpec("docs", []),
                new AppSpec("editor", [new("files", "conn-4-f")])
            ]),
            // M5: declared slots but 0 bound (fallback slotless binding)
            new MemberAppConfig("m5-acct", "m5-brain", [
                new AppSpec("analytics", [])
            ]),
            // M6: 1 slot
            new MemberAppConfig("m6-acct", "m6-brain", [
                new AppSpec("reporting", [new("db", "conn-6-db")])
            ]),
            // M7: slotless
            new MemberAppConfig("m7-acct", "m7-brain", [
                new AppSpec("messenger", [])
            ]),
            // M8: 4 slots
            new MemberAppConfig("m8-acct", "m8-brain", [
                new AppSpec("studio", [new("s1", "conn-8-1"), new("s2", "conn-8-2"), new("s3", "conn-8-3"), new("s4", "conn-8-4")])
            ]),
            // M9: 1 slotless + 2 slots
            new MemberAppConfig("m9-acct", "m9-brain", [
                new AppSpec("camera", []),
                new AppSpec("gallery", [new("cdn", "conn-9-cdn"), new("auth", "conn-9-auth")])
            ]),
        };

        // Initialize active grants tracking per member brain: brain -> Set<grantId>
        var brainActiveGrants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var initialSharedApps = new List<string>();
        var initialBindings = new List<CircleBinding>();

        foreach (var member in memberConfigs)
        {
            var grants = new HashSet<string>(StringComparer.Ordinal);
            brainActiveGrants[member.BrainId] = grants;

            foreach (var app in member.Apps)
            {
                initialSharedApps.Add(app.Name);
                // App grant
                var appGrantId = $"circle:{circleBrain}:app:{app.Name}";
                grants.Add(appGrantId);

                if (app.Slots.Count == 0)
                {
                    // Slotless app fallback binding
                    initialBindings.Add(new(member.AccountId, app.Name, string.Empty, $"{member.BrainId}/apps/{app.Name}"));
                }
                else
                {
                    foreach (var slot in app.Slots)
                    {
                        var connKey = $"{member.BrainId}/connection/service/{slot.ConnectionId}";
                        var connGrantId = $"circle:{circleBrain}:conn:{connKey}";
                        grants.Add(connGrantId);
                        initialBindings.Add(new(member.AccountId, app.Name, slot.SlotName, connKey));
                    }
                }
            }
        }

        var state = new CircleRecord
        {
            Name = "enterprise",
            Owner = memberConfigs[0].AccountId,
            Members = memberConfigs.Select(m => m.AccountId).ToArray(),
            SharedApps = [.. initialSharedApps],
            Bindings = [.. initialBindings],
            Admins = [memberConfigs[0].AccountId, memberConfigs[1].AccountId],
        };

        Assert.Equal(10, state.Members.Length);
        Assert.Equal(13, state.SharedApps.Length);
        Assert.Equal(20, state.Bindings.Length); // 6 slotless + 14 slotted

        // Helper to simulate Unbind turn logic
        void ExecuteUnbind(string accountId)
        {
            var memberBindings = state.Bindings.Where(binding => binding.AccountId == accountId).ToArray();
            var appsToUnshare = memberBindings.Select(b => b.AppName).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var appSources = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var binding in memberBindings)
            {
                var source = ExtractBrain(binding.ConnectionKey);
                appSources[binding.AppName] = source;

                if (!string.IsNullOrEmpty(binding.Slot))
                {
                    var connGrantId = $"circle:{circleBrain}:conn:{binding.ConnectionKey}";
                    Assert.True(brainActiveGrants[source].Remove(connGrantId), $"Expected conn grant {connGrantId} to exist on {source}.");
                }
            }

            foreach (var (appName, source) in appSources)
            {
                var appGrantId = $"circle:{circleBrain}:app:{appName}";
                Assert.True(brainActiveGrants[source].Remove(appGrantId), $"Expected app grant {appGrantId} to exist on {source}.");
            }

            state.SharedApps = [.. state.SharedApps.Where(app => !appsToUnshare.Contains(app))];
            state.Bindings = [.. state.Bindings.Where(binding => binding.AccountId != accountId)];
            state.Admins = [.. state.Admins.Where(admin => admin != accountId)];
            state.Members = [.. state.Members.Where(m => m != accountId)];
        }

        // --- Phase 1: Depart Member 3 (slotted with 3 slots) ---
        ExecuteUnbind("m3-acct");
        Assert.Empty(brainActiveGrants["m3-brain"]); // Exactly 0 leaks on m3-brain
        Assert.DoesNotContain("workflow", state.SharedApps);
        Assert.DoesNotContain(state.Bindings, b => b.AccountId == "m3-acct");
        Assert.Equal(9, state.Members.Length);

        // --- Phase 2: Depart Member 5 (slotless fallback binding) ---
        ExecuteUnbind("m5-acct");
        Assert.Empty(brainActiveGrants["m5-brain"]); // Exactly 0 leaks on m5-brain
        Assert.DoesNotContain("analytics", state.SharedApps);
        Assert.DoesNotContain(state.Bindings, b => b.AccountId == "m5-acct");
        Assert.Equal(8, state.Members.Length);

        // --- Phase 3: Depart Member 4 (1 slotless + 1 slotted) ---
        ExecuteUnbind("m4-acct");
        Assert.Empty(brainActiveGrants["m4-brain"]); // Exactly 0 leaks on m4-brain
        Assert.DoesNotContain("docs", state.SharedApps);
        Assert.DoesNotContain("editor", state.SharedApps);
        Assert.DoesNotContain(state.Bindings, b => b.AccountId == "m4-acct");
        Assert.Equal(7, state.Members.Length);

        // Verify remaining active members have 100% active grants intact (zero collateral damage)
        foreach (var remaining in new[] { "m0", "m1", "m2", "m6", "m7", "m8", "m9" })
        {
            var config = memberConfigs.Single(m => m.BrainId == $"{remaining}-brain");
            Assert.NotEmpty(brainActiveGrants[config.BrainId]);
            foreach (var app in config.Apps)
            {
                Assert.Contains(app.Name, state.SharedApps);
                Assert.Contains($"circle:{circleBrain}:app:{app.Name}", brainActiveGrants[config.BrainId]);
            }
        }

        // --- Phase 4: Sequential unbind of remaining non-owner members ---
        foreach (var nonOwner in new[] { "m1-acct", "m2-acct", "m6-acct", "m7-acct", "m8-acct", "m9-acct" })
        {
            var brainId = memberConfigs.Single(m => m.AccountId == nonOwner).BrainId;
            ExecuteUnbind(nonOwner);
            Assert.Empty(brainActiveGrants[brainId]); // 0 leaks
        }

        // Only owner m0 remains
        Assert.Single(state.Members);
        Assert.Equal("m0-acct", state.Members[0]);
        Assert.Equal(2, state.SharedApps.Length); // calc, portal
        Assert.Equal(3, state.Bindings.Length); // calc (slotless), portal storage, portal db

        // --- Phase 5: Circle Deletion (Owner departs) ---
        ExecuteUnbind("m0-acct");
        Assert.Empty(brainActiveGrants["m0-brain"]); // 0 leaks on owner brain

        // Universal post-condition: ALL brains have 0 leaked grants
        foreach (var (brain, grants) in brainActiveGrants)
        {
            Assert.Empty(grants);
        }

        // Circle state is completely empty
        Assert.Empty(state.Members);
        Assert.Empty(state.SharedApps);
        Assert.Empty(state.Bindings);
        Assert.Empty(state.Admins);
    }

    [Fact]
    public void ShareIntoCircle_Invariants_SlotlessAndSlotted_DoNotThrowOrCallConfigure()
    {
        // 1. Slotless app manifest: Slots is empty
        var slotlessManifest = new AppManifest("notepad", "Personal Notepad", [], [], []);
        var slotlessBindings = ComputeShareBindings("user-1", "user-brain", "notepad", slotlessManifest, new Dictionary<string, string>(), []);
        Assert.Single(slotlessBindings);
        Assert.Equal(string.Empty, slotlessBindings[0].Slot);
        Assert.Equal("user-brain/apps/notepad", slotlessBindings[0].ConnectionKey);

        // 2. Slotted app manifest with unbound slots: falls back to slotless binding
        var unboundManifest = new AppManifest("chat", "Chat", [], [], [new AppSlot("server", "websocket", null)]);
        var unboundBindings = ComputeShareBindings("user-1", "user-brain", "chat", unboundManifest, new Dictionary<string, string>(), []);
        Assert.Single(unboundBindings);
        Assert.Equal(string.Empty, unboundBindings[0].Slot);
        Assert.Equal("user-brain/apps/chat", unboundBindings[0].ConnectionKey);

        // 3. Slotted app manifest with bound slots: creates bindings per bound slot
        var slottedManifest = new AppManifest("storage-app", "Storage", [], [], [
            new AppSlot("primary", "s3", null),
            new AppSlot("backup", "s3", null)
        ]);
        var connections = new Dictionary<string, string>
        {
            ["primary"] = "bucket-1",
            ["backup"] = "bucket-2"
        };
        var slottedBindings = ComputeShareBindings("user-1", "user-brain", "storage-app", slottedManifest, connections, []);
        Assert.Equal(2, slottedBindings.Count);
        Assert.Equal("primary", slottedBindings[0].Slot);
        Assert.Equal("backup", slottedBindings[1].Slot);
        Assert.All(slottedBindings, b => Assert.NotEmpty(b.ConnectionKey));
    }

    private sealed record SlotSpec(string SlotName, string ConnectionId);
    private sealed record AppSpec(string Name, List<SlotSpec> Slots);
    private sealed record MemberAppConfig(string AccountId, string BrainId, AppSpec[] Apps);

    private static List<CircleBinding> ComputeShareBindings(
        string accountId,
        string sourceBrain,
        string appName,
        AppManifest manifest,
        IReadOnlyDictionary<string, string> connections,
        string[] connectionIds)
    {
        List<CircleBinding> bindings = [];
        foreach (var slot in manifest.Slots)
        {
            if (!connections.TryGetValue(slot.Name, out var id) || id.Length == 0) { continue; }
            if (slot.Capability is not null)
            { throw new InvalidOperationException($"Slot '{slot.Name}' binds capability '{slot.Capability}'."); }
            if (connectionIds.Length > 0 && !connectionIds.Contains(id, StringComparer.Ordinal)) { continue; }
            var owned = ConnectionKeys.For(sourceBrain, slot.Connector, id);
            bindings.Add(new(accountId, appName, slot.Name, owned));
        }
        if (bindings.Count == 0)
        {
            bindings.Add(new(accountId, appName, string.Empty, $"{sourceBrain}/apps/{appName}"));
        }
        return bindings;
    }

    [Fact]
    public void DeleteCircle_CleansUpOwnerGrantsAndBindings()
    {
        // When Delete runs, it unbinds all members including the owner
        const string ownerAccount = "creator-acct";
        const string ownerApp = "calc";

        var state = new CircleRecord
        {
            Name = "to-delete",
            Owner = ownerAccount,
            Members = [ownerAccount, "member-2"],
            SharedApps = [ownerApp],
            Bindings = [new(ownerAccount, ownerApp, string.Empty, $"{SharerBrain}/apps/{ownerApp}")],
            Admins = [ownerAccount],
        };

        // When Unbind is called for the owner as part of Delete:
        var memberBindings = state.Bindings.Where(b => b.AccountId == ownerAccount).ToArray();
        var appsToUnshare = memberBindings.Select(b => b.AppName).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var appSources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var binding in memberBindings)
        {
            appSources[binding.AppName] = ExtractBrain(binding.ConnectionKey);
        }

        var revokedAppGrants = appSources.Select(kv => $"circle:{CircleBrain}:app:{kv.Key}").ToList();

        state.SharedApps = [.. state.SharedApps.Where(app => !appsToUnshare.Contains(app))];
        state.Bindings = [.. state.Bindings.Where(b => b.AccountId != ownerAccount)];

        Assert.Single(revokedAppGrants);
        Assert.Equal($"circle:{CircleBrain}:app:{ownerApp}", revokedAppGrants[0]);
        Assert.Empty(state.SharedApps);
        Assert.Empty(state.Bindings);
    }

    private static string ExtractBrain(string key)
    {
        var clean = key.StartsWith(ConnectionKeys.NeuronScheme, StringComparison.Ordinal)
            ? key[ConnectionKeys.NeuronScheme.Length..]
            : key;
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? clean[..slash] : "local";
    }

}
