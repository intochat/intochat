using System.Text.Json;

namespace DigitalBrain.Microsoft.Azure.Install;

// deployment/<env>.json: what the OS foundation needs that the code cannot know.
public sealed record InstallConfig
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public string Subscription { get; init; } = "";
    public string Location { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Env { get; init; } = "";
    public string OsImage { get; init; } = "";
    public double Cpu { get; init; } = 0.5;
    public string Memory { get; init; } = "1Gi";
    public int StorageQuotaGb { get; init; } = 20;
    // Optional: the registry that holds OsImage, pulled with the runtime identity.
    public string RegistryServer { get; init; } = "";
    // Orleans__ClusterId and Orleans__ServiceId; default to intochat-<env>.
    public string ClusterId { get; init; } = "";
    public string ServiceId { get; init; } = "";
    // Suffix of every versioned store: grainstate-v1, brainlease-v1, /data/brain/v1.
    public string StorageFormat { get; init; } = "v1";

    public static InstallConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<InstallConfig>(json, Json) ?? throw new InvalidOperationException("The install config is empty.");
        return config.Validated();
    }

    public InstallConfig Validated()
    {
        foreach (var (name, value) in new[]
        {
            ("subscription", Subscription), ("location", Location), ("prefix", Prefix), ("env", Env), ("osImage", OsImage),
        })
        {
            if (string.IsNullOrWhiteSpace(value)) { throw new InvalidOperationException($"The install config needs '{name}'."); }
        }
        if (Cpu <= 0 || StorageQuotaGb <= 0) { throw new InvalidOperationException("The install config needs a positive cpu and storageQuotaGb."); }
        return this with
        {
            ClusterId = ClusterId.Length > 0 ? ClusterId : "intochat-" + Env,
            ServiceId = ServiceId.Length > 0 ? ServiceId : "intochat-" + Env,
        };
    }
}
