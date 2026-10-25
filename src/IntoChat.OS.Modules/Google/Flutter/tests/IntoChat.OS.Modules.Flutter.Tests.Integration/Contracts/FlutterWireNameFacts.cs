using System.Reflection;
using System.Text.Json.Nodes;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.WebBrowser;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Contracts;

// W2 PR 0 (ui-contract 6.1, 7.1): the names the shell and stored brains carry for the IntoChat.OS.Modules.Flutter.*
// contracts are pinned exactly, so the Ui contracts move (PR 2) and the generator (PR 3) can prove they changed
// nothing. Per public type: contract full name, Orleans alias, default grain type, method aliases and the state Read
// returns; signal short names (Watch selection and SignalEnvelope.type use them); enum member names. W1's
// ContractManifest pins field ids repo-wide and only for additions; this pin is exact, rename-or-add both fail.
public sealed class FlutterWireNameFacts
{
    internal const string GoldenFile = "flutter-wire-names.golden.json";
    internal const string EnumBytesFile = "flutter-aliasless-enums.golden.json";

    // The silo's serializer: Orleans plus the legacy type names BrainHosting registers (0.7.0 namespace moves).
    private static readonly Serializer Serializer = new ServiceCollection().AddSerializer()
        .AddSingleton<ITypeConverter, LegacyTypeNames>().BuildServiceProvider().GetRequiredService<Serializer>();

    internal static SortedDictionary<string, string> Capture()
    {
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in typeof(UIVocabulary).Assembly.GetExportedTypes()
                     .Where(type => type.Namespace?.StartsWith("IntoChat.OS.Modules.Flutter", StringComparison.Ordinal) == true))
        {
            names[type.FullName!] = Describe(type);
        }
        return names;
    }

    private static string Describe(Type type)
    {
        var alias = type.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias ?? "-";
        if (type.IsInterface && typeof(INeuron).IsAssignableFrom(type))
        {
            var grain = type.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType == typeof(Orleans.Metadata.DefaultGrainTypeAttribute))?.ConstructorArguments[0].Value as string;
            var methods = type.GetMethods().OrderBy(Signature, StringComparer.Ordinal)
                .Select(method => Signature(method) + "=" + (method.GetCustomAttribute<AliasAttribute>()?.Alias ?? "-"));
            var read = type.GetMethod("Read", Type.EmptyTypes)?.ReturnType is { IsGenericType: true } task ? task.GetGenericArguments()[0].FullName : "-";
            return $"contract; alias={alias}; grain={grain ?? "-"}; read={read}; methods={string.Join(",", methods)}";
        }
        if (typeof(Signal).IsAssignableFrom(type)) { return $"signal; short={type.Name}; alias={alias}"; }
        if (type.IsEnum) { return $"enum; alias={alias}; serialized={Serialized(type)}; members={string.Join(",", Enum.GetNames(type))}"; }
        return $"{(type.IsInterface ? "interface" : type.IsAbstract && type.IsSealed ? "static" : "type")}; alias={alias}; serialized={Serialized(type)}";
    }

    private static bool Serialized(Type type) => type.IsDefined(typeof(GenerateSerializerAttribute), inherit: false);

    private static string Signature(MethodInfo method) => $"{method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})";

    [Fact]
    public void EveryFlutterWireNameIsUnchanged()
    {
        var golden = UiContractFiles.Golden(GoldenFile).ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<string>(), StringComparer.Ordinal);
        var current = Capture();

        var changes = golden.Where(entry => !current.TryGetValue(entry.Key, out var now) || now != entry.Value)
            .Select(entry => current.TryGetValue(entry.Key, out var now) ? $"{entry.Key}: \"{entry.Value}\" -> \"{now}\"" : $"{entry.Key}: removed")
            .Concat(current.Keys.Except(golden.Keys).Select(key => $"{key}: added (review and capture)"))
            .ToArray();
        Assert.True(changes.Length == 0, "IntoChat.OS.Modules.Flutter wire names changed; clients and stored brains still carry the old ones:\n" + string.Join('\n', changes));
    }

    // The capture finds what the W2 audit counted, so the fact above cannot pass vacuously.
    [Fact]
    public void ThePinCoversTheUiContracts()
    {
        var golden = UiContractFiles.Golden(GoldenFile);

        Assert.Equal(42, golden.Count(entry => entry.Value!.GetValue<string>().StartsWith("contract;", StringComparison.Ordinal)));
        Assert.StartsWith("contract; alias=button; grain=button; read=IntoChat.OS.Modules.Flutter.Button.ButtonState;", golden["IntoChat.OS.Modules.Flutter.Button.IButton"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(golden, entry => entry.Value!.GetValue<string>().StartsWith("enum; alias=-; serialized=True", StringComparison.Ordinal));
        Assert.Equal(["IntoChat.OS.Modules.Flutter.Chat.ChatRole", "IntoChat.OS.Modules.Flutter.Chat.ChatRunStatus", "IntoChat.OS.Modules.Flutter.Entries.EntryControlMode", "IntoChat.OS.Modules.Flutter.Preferences.PreferenceKind", "IntoChat.OS.Modules.Flutter.Preferences.PreferenceStore", "IntoChat.OS.Modules.Flutter.WebBrowser.BrowserConnection"],
            golden.Where(entry => entry.Value!.GetValue<string>().StartsWith("enum; alias=ui.", StringComparison.Ordinal) && entry.Value!.GetValue<string>().Contains("serialized=True", StringComparison.Ordinal)).Select(entry => entry.Key));
    }

    // ChatRole, ChatRunStatus and BrowserConnection are [GenerateSerializer] without an alias, so the bytes written for
    // them as object carry the CLR type name and assembly: the stored form the PR 2 move must keep resolving.
    public static TheoryData<string> AliaslessEnums() => ["declared:ChatRole", "object:ChatRole", "declared:ChatRunStatus", "object:ChatRunStatus", "declared:BrowserConnection", "object:BrowserConnection"];

    private static Type Form(string key) => key.StartsWith("object:", StringComparison.Ordinal) ? typeof(object) : EnumSample(key).GetType();

    private static object EnumSample(string key) => key.Split(':')[1] switch
    {
        "ChatRole" => ChatRole.Assistant,
        "ChatRunStatus" => ChatRunStatus.Interrupted,
        "BrowserConnection" => BrowserConnection.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private static byte[] EnumBytes(string key)
        => (byte[])Generic(nameof(Serializer.SerializeToArray), Form(key)).Invoke(Serializer, [EnumSample(key)])!;

    private static object EnumRead(string key, byte[] bytes)
        => Generic(nameof(Serializer.Deserialize), Form(key)).Invoke(Serializer, [bytes])!;

    private static MethodInfo Generic(string name, Type type) => typeof(Serializer).GetMethods()
        .Single(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters() is [{ } only] && (only.ParameterType.IsGenericParameter || only.ParameterType == typeof(byte[])))
        .MakeGenericMethod(type);

    [Theory]
    [MemberData(nameof(AliaslessEnums))]
    public void TheAliaslessEnumKeepsItsStoredBytes(string key)
    {
        var golden = UiContractFiles.Golden(EnumBytesFile)[key]!.GetValue<string>();

        Assert.Equal(golden, Convert.ToHexString(EnumBytes(key)));
        Assert.Equal(EnumSample(key), EnumRead(key, Convert.FromHexString(golden)));
    }

    // Bytes written before the three enums gained an alias (CLR full name and assembly as the type name). Hex only,
    // so no token rename can alter the literals.
    [Theory]
    [InlineData("object:ChatRole", "100199FE0BA38F496E746F436861742E4F532E466C75747465722E436861742E43686174526F6C652C496E746F436861742E4F532E4D6F64756C65732E466C75747465722E436F6E74726163747305")]
    [InlineData("object:ChatRunStatus", "1001552EDFA599496E746F436861742E4F532E466C75747465722E436861742E4368617452756E5374617475732C496E746F436861742E4F532E4D6F64756C65732E466C75747465722E436F6E74726163747311")]
    [InlineData("object:BrowserConnection", "100114A2474FAD496E746F436861742E4F532E466C75747465722E57656242726F777365722E42726F77736572436F6E6E656374696F6E2C496E746F436861742E4F532E4D6F64756C65732E466C75747465722E436F6E7472616374730D")]
    public void BytesWrittenBeforeTheAliasStillRead(string key, string legacyHex)
        => Assert.Equal(EnumSample(key), EnumRead(key, Convert.FromHexString(legacyHex)));

    [Fact(Explicit = true)]
    public void CaptureFlutterWireNames()
    {
        var names = new JsonObject();
        foreach (var (key, value) in Capture()) { names[key] = value; }
        UiContractFiles.Write(Path.Combine("Contracts", GoldenFile), names);
        var bytes = new JsonObject();
        foreach (var key in AliaslessEnums().Select(row => row.Data)) { bytes[key] = Convert.ToHexString(EnumBytes(key)); }
        UiContractFiles.Write(Path.Combine("Contracts", EnumBytesFile), bytes);
    }
}
