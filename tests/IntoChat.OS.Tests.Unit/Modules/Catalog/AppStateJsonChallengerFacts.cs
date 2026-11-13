using System.Text.Json;
using IntoChat.OS.Connections;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppStateJsonChallengerFacts
{
    [Fact]
    public void StringEntry_ToMap_NullAndEmptyArrays_ReturnEmptyDictionary()
    {
        // 1. Single-argument overload with null
        var map1 = StringEntry.ToMap(null);
        Assert.NotNull(map1);
        Assert.Empty(map1);

        // 2. Single-argument overload with empty array
        var map2 = StringEntry.ToMap([]);
        Assert.NotNull(map2);
        Assert.Empty(map2);

        // 3. Dual-argument overload with null entries and null legacy
        var map3 = StringEntry.ToMap(null, null);
        Assert.NotNull(map3);
        Assert.Empty(map3);

        // 4. Dual-argument overload with empty array and null legacy
        var map4 = StringEntry.ToMap([], null);
        Assert.NotNull(map4);
        Assert.Empty(map4);
    }

    [Fact]
    public void StringEntry_ToMap_FallbackToLegacy_WhenEntriesNullOrEmpty()
    {
        var legacy = new Dictionary<string, string> { ["legacyKey"] = "legacyVal" };

        var mapFromNull = StringEntry.ToMap(null, legacy);
        Assert.Single(mapFromNull);
        Assert.Equal("legacyVal", mapFromNull["legacyKey"]);

        var mapFromEmpty = StringEntry.ToMap([], legacy);
        Assert.Single(mapFromEmpty);
        Assert.Equal("legacyVal", mapFromEmpty["legacyKey"]);
    }

    [Fact]
    public void StringEntry_ToMap_EntriesWinOverLegacy()
    {
        var entries = new[] { new StringEntry("canonicalKey", "canonicalVal") };
        var legacy = new Dictionary<string, string> { ["legacyKey"] = "legacyVal" };

        var map = StringEntry.ToMap(entries, legacy);
        Assert.Single(map);
        Assert.Equal("canonicalVal", map["canonicalKey"]);
        Assert.False(map.ContainsKey("legacyKey"));
    }

    [Fact]
    public void StringEntry_ToMap_DuplicateKeys_LastWriteWinsWithoutThrowing()
    {
        var entries = new[]
        {
            new StringEntry("dup", "first"),
            new StringEntry("other", "val"),
            new StringEntry("dup", "second")
        };

        var map = StringEntry.ToMap(entries);
        Assert.Equal(2, map.Count);
        Assert.Equal("second", map["dup"]);
        Assert.Equal("val", map["other"]);
    }

    [Fact]
    public void AppState_DeserializesLegacyJsonWithRetiredCollections_GracefullyIgnoresOldProperties()
    {
        // Simulates old serialized payload that still contains LegacyFiles, LegacySettings, LegacyConnections
        var legacyJson = """
        {
            "Spec": "echo-spec",
            "Version": "1.0.0",
            "Status": 1,
            "LegacyFiles": {
                "old/path.cs": "// old code"
            },
            "LegacySettings": {
                "oldSetting": "oldValue"
            },
            "LegacyConnections": {
                "oldConn": "oldTarget"
            },
            "FileEntries": [
                { "Key": "new/path.cs", "Value": "// new code" }
            ],
            "SettingEntries": [
                { "Key": "newSetting", "Value": "newValue" }
            ],
            "ConnectionEntries": [
                { "Key": "newConn", "Value": "newTarget" }
            ]
        }
        """;

        var state = JsonSerializer.Deserialize<AppState>(legacyJson, DigitalBrain.Invocation.RuntimeProtocol.Json);
        Assert.NotNull(state);
        Assert.Equal("1.0.0", state.Version);
        Assert.Equal("echo-spec", state.Spec);

        // Canonical collections read from FileEntries/SettingEntries/ConnectionEntries
        Assert.Single(state.Files);
        Assert.Equal("// new code", state.Files["new/path.cs"]);

        Assert.Single(state.Settings);
        Assert.Equal("newValue", state.Settings["newSetting"]);

        Assert.Single(state.Connections);
        Assert.Equal("newTarget", state.Connections["newConn"]);
    }

    [Fact]
    public void AppState_DeserializesLegacyJsonWithoutNewEntries_ProducesCleanEmptyDictionaries()
    {
        // Old payload that only had Legacy collections and no new entries
        var pureOldJson = """
        {
            "Spec": "legacy-only-spec",
            "Version": "0.9.0",
            "LegacyFiles": {
                "file.cs": "code"
            },
            "LegacySettings": {
                "s": "v"
            }
        }
        """;

        var state = JsonSerializer.Deserialize<AppState>(pureOldJson, DigitalBrain.Invocation.RuntimeProtocol.Json);
        Assert.NotNull(state);
        Assert.Equal("0.9.0", state.Version);
        // Retired legacy collections are ignored; state defaults to clean empty dictionaries
        Assert.Empty(state.Files);
        Assert.Empty(state.Settings);
        Assert.Empty(state.Connections);
    }

    [Fact]
    public void AppState_SpecialCharactersAndUnicodeInEntries_RoundTripIntact()
    {
        var state = new AppState();
        state.PutFile("synapses/spécial char/🚀 emoji.cs", "public class 🚀 { string s = \"line1\nline2\t\\\"\"; }");
        state.PutSetting("setting:with:colons", "val with = and ; and spaces");
        state.PutConnection("conn/with/slashes", "target://host:8080/path?query=1");

        var json = JsonSerializer.Serialize(state, DigitalBrain.Invocation.RuntimeProtocol.Json);
        var read = JsonSerializer.Deserialize<AppState>(json, DigitalBrain.Invocation.RuntimeProtocol.Json);

        Assert.NotNull(read);
        Assert.Equal(state.Files["synapses/spécial char/🚀 emoji.cs"], read.Files["synapses/spécial char/🚀 emoji.cs"]);
        Assert.Equal(state.Settings["setting:with:colons"], read.Settings["setting:with:colons"]);
        Assert.Equal(state.Connections["conn/with/slashes"], read.Connections["conn/with/slashes"]);
    }

    [Fact]
    public void AppState_Clone_DeepCopiesEntryArrays()
    {
        var state = new AppState();
        state.PutFile("file1.cs", "v1");
        state.PutSetting("setting1", "s1");
        state.PutConnection("conn1", "c1");

        var clone = state.Clone();

        // Mutate original
        state.PutFile("file1.cs", "v2");
        state.PutFile("file2.cs", "added");
        state.PutSetting("setting1", "s2");
        state.PutConnection("conn1", "c2");

        // Clone should retain original values
        Assert.Equal("v1", clone.Files["file1.cs"]);
        Assert.False(clone.Files.ContainsKey("file2.cs"));
        Assert.Equal("s1", clone.Settings["setting1"]);
        Assert.Equal("c1", clone.Connections["conn1"]);
    }

    [Fact]
    public void AppVersionRecord_RoundTripsThroughJson_WithFileEntries()
    {
        var manifest = new AppManifest("Title", "Desc", [], [], []);
        var files = new Dictionary<string, string>
        {
            ["app.cs"] = "// app code",
            ["synapse.cs"] = "// synapse"
        };
        var record = new AppVersionRecord("2.0.0", DateTimeOffset.UnixEpoch, manifest, "spec", files);

        var json = JsonSerializer.Serialize(record, DigitalBrain.Invocation.RuntimeProtocol.Json);
        var read = JsonSerializer.Deserialize<AppVersionRecord>(json, DigitalBrain.Invocation.RuntimeProtocol.Json);

        Assert.NotNull(read);
        Assert.Equal("2.0.0", read.Version);
        Assert.Equal("spec", read.Spec);
        Assert.Equal(2, read.Files.Count);
        Assert.Equal("// app code", read.Files["app.cs"]);
        Assert.Equal("// synapse", read.Files["synapse.cs"]);
    }

    [Fact]
    public void AppVersionRecord_DeserializesLegacyJsonWithLegacyFilesGracefully()
    {
        // When reading an older version record JSON that contained LegacyFiles
        var oldRecordJson = """
        {
            "Version": "1.5.0",
            "SavedAt": "2026-01-01T00:00:00+00:00",
            "Manifest": {
                "Title": "Old App",
                "Description": "Old",
                "Actions": [],
                "Configurations": [],
                "Connections": []
            },
            "Spec": "old-spec",
            "LegacyFiles": {
                "old.cs": "// old"
            },
            "FileEntries": [
                { "Key": "migrated.cs", "Value": "// migrated" }
            ]
        }
        """;

        var read = JsonSerializer.Deserialize<AppVersionRecord>(oldRecordJson, DigitalBrain.Invocation.RuntimeProtocol.Json);
        Assert.NotNull(read);
        Assert.Equal("1.5.0", read.Version);
        Assert.Single(read.Files);
        Assert.Equal("// migrated", read.Files["migrated.cs"]);
    }
}
