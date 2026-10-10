using System.Reflection;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Framework.Platform.Secrets;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Keys;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R30/R46: no credential reaches the trust host. Its composition registers no secret store, key wrapper, custody or
// integration credentials, and nothing a machine sends it through a client is plaintext or an unwrapped key.
public sealed class TrustBoundaryFacts
{
    [Fact]
    public void TheTrustHostRegistersNoSecretOrCustodyService()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(silo => silo.AddTrustCluster());
        Type[] forbidden = [typeof(ISecrets), typeof(IKeyWrapper), typeof(IMasterKeyCustody)];

        Assert.DoesNotContain(builder.Services, descriptor => forbidden.Contains(descriptor.ServiceType)
            || descriptor.ServiceType.IsGenericType && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IModuleIntegrations<>));
    }

    // Records that cross a client and are known to carry identifiers, sealed bytes or signatures only. Extending this
    // list needs a controller ruling; the identity records joined it with R58.
    private static readonly Type[] Records =
    [
        typeof(HeadRecord), typeof(StorageNodeView), typeof(MarketplaceRecord), typeof(MachineSigned<>),
        typeof(Account), typeof(Member), typeof(BrainProvision), typeof(PrincipalCredential),
        typeof(CallRequest), typeof(CallDecision), typeof(CallerContext), typeof(Grant),
    ];

    // Byte arrays that may cross, by parameter or member name: wrapped keys, sealed shards and heads, public keys,
    // signatures, proof nonces and the pairing secret's hash (R52).
    private static readonly string[] Bytes =
        ["wrapped", "sealedShard", "publicKey", "selfSignature", "nonce", "pairingSecretHash", "HolderKey", "Payload", "Signature"];

    // What a returned byte array is: a shard as stored, a storage proof, a wrapped part key.
    private static readonly Dictionary<string, string> Returns = new(StringComparer.Ordinal)
    {
        ["INodeDirectoryClient.Get"] = "sealedShard", ["INodeDirectoryClient.Prove"] = "nonce", ["IKeyringClient.Get"] = "wrapped",
    };

    private static readonly string[] Plaintext = ["Password", "Secret", "Token", "Credential", "MasterKey"];

    [Fact]
    public void NoTrustClientCarriesPlaintextOrKeys()
    {
        Type[] clients = [typeof(ISequencerClient), typeof(INodeDirectoryClient), typeof(IKeyringClient), typeof(IMarketplaceClient), typeof(IIdentityClient)];
        var offenders = new List<string>();
        foreach (var method in clients.SelectMany(client => client.GetMethods()))
        {
            foreach (var parameter in method.GetParameters()) { Check(parameter.ParameterType, parameter.Name!, $"{method.DeclaringType!.Name}.{method.Name}({parameter.Name})", offenders, []); }
            var at = $"{method.DeclaringType!.Name}.{method.Name}";
            Check(method.ReturnType, Returns.GetValueOrDefault(at, ""), $"{at} returns", offenders, []);
        }

        Assert.Empty(offenders);
    }

    private static void Check(Type type, string name, string at, List<string> offenders, HashSet<Type> seen)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) { type = type.GetGenericArguments()[0]; }
        if (type == typeof(Task) || type == typeof(void)) { return; }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(byte[]))
        {
            if (!Bytes.Contains(name, StringComparer.OrdinalIgnoreCase)) { offenders.Add($"{at}: bytes named '{name}'"); }
            return;
        }
        if (type.IsArray) { Check(type.GetElementType()!, name, at, offenders, seen); return; }
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTimeOffset) || type == typeof(Guid))
        { return; }
        if (type == typeof(string))
        {
            // A hash is not the credential it hashes.
            if (Plaintext.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)) && !name.EndsWith("Hash", StringComparison.OrdinalIgnoreCase))
            { offenders.Add($"{at}: plaintext string '{name}'"); }
            return;
        }
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (!Records.Contains(definition)) { offenders.Add($"{at}: {type.Name} is not an allowed trust record"); return; }
        if (!seen.Add(type)) { return; }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        { Check(property.PropertyType, property.Name, $"{at} -> {type.Name}.{property.Name}", offenders, seen); }
    }
}
