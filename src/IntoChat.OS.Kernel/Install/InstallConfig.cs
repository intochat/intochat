using System.Text.Json;
using System.Text.RegularExpressions;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Kernel.Install;

// deployment/<env>.json: what the OS foundation needs that the code cannot know.
public sealed partial record InstallConfig
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public string Subscription { get; init; } = "";
    public string Location { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Env { get; init; } = "";
    public string OsImage { get; init; } = "";
    public string CloudImage { get; init; } = "";
    public string SandboxImage { get; init; } = "";
    public double Cpu { get; init; } = 0.5;
    public string Memory { get; init; } = "1Gi";
    public double CloudCpu { get; init; } = 0.25;
    public string CloudMemory { get; init; } = "0.5Gi";
    public double SandboxCpu { get; init; } = 0.25;
    public string SandboxMemory { get; init; } = "0.5Gi";
    public int StorageQuotaGb { get; init; } = 20;
    // Optional: the registry that holds OsImage, pulled with the runtime identity.
    public string RegistryServer { get; init; } = "";
    // Orleans__ClusterId and Orleans__ServiceId; default to intochat-<env>.
    public string ClusterId { get; init; } = "";
    public string ServiceId { get; init; } = "";
    // Suffix of every versioned store: grainstate-v1, brainlease-v1, /data/brain/v1.
    public string StorageFormat { get; init; } = "v1";
    // Optional explicit names. Empty means the derived <prefix>-<env>-<kind>.
    public string ResourceGroup { get; init; } = "";
    // Keys: os, identity, storage, vault, environment, logs, registry, postgres, foundry.
    public Dictionary<string, string> Names { get; init; } = [];
    public RegistryConfig Registry { get; init; } = new();
    public PostgresConfig Postgres { get; init; } = new();
    public FoundryConfig Foundry { get; init; } = new();
    public LogWorkspaceConfig LogWorkspace { get; init; } = new();
    public CostGuardrailConfig CostGuardrails { get; init; } = new();
    public DevSlotJobConfig DevSlotJob { get; init; } = new();
    // Object id of the principal running the install (the gh-intochat-platform OIDC app). It is granted Key Vault
    // Secrets Officer on the vault so the installer can read and write secret values on the data plane.
    public string InstallerPrincipalId { get; init; } = "";
    // When set, the OS app also pulls from ghcr.io as this user with the Key Vault secret ghcr-read-token.
    public string GhcrUsername { get; init; } = "";
    // Settings per module id, read by that module's deployment: { "<module>": { "<key>": "..." } }.
    public Dictionary<string, Dictionary<string, string>> Modules { get; init; } = [];

    public static InstallConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<InstallConfig>(json, Json) ?? throw new InvalidOperationException("The install config is empty.");
        return config.Validated();
    }

    /// <summary>
    /// Universal environment configuration builder: derives all resource names, registry, storage,
    /// Postgres, and container images from <paramref name="env"/> and <paramref name="prefix"/> without
    /// requiring a static <c>deploy/&lt;env&gt;.json</c> file on disk.
    /// </summary>
    public static InstallConfig Universal(
        string env,
        string? subscription = null,
        string? location = null,
        string? prefix = null,
        bool includeCloudApp = false,
        bool includeSandboxApp = false,
        string? publicBaseUrl = null,
        bool includeDevSlotJob = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(env);
        var resolvedPrefix = string.IsNullOrWhiteSpace(prefix) ? "intochat" : prefix.Trim();
        var resolvedEnv = env.Trim().ToLowerInvariant();
        var resolvedSubscription = !string.IsNullOrWhiteSpace(subscription)
            ? subscription.Trim()
            : Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID")
                ?? TryResolveAzCliSubscription()
                ?? "2a985480-6bb9-44ff-959d-edd5efe2cdb2";
        var resolvedLocation = !string.IsNullOrWhiteSpace(location)
            ? location.Trim()
            : Environment.GetEnvironmentVariable("AZURE_LOCATION") ?? "polandcentral";

        var registryName = ResourceNames.Registry(resolvedPrefix, resolvedEnv);
        var registryServer = $"{registryName}.azurecr.io";
        var resourceGroup = ResourceNames.ResourceGroup(resolvedPrefix, resolvedEnv);

        var resolvedPublicUrl = !string.IsNullOrWhiteSpace(publicBaseUrl)
            ? publicBaseUrl.Trim()
            : string.Equals(resolvedEnv, "prod", StringComparison.OrdinalIgnoreCase)
                ? "https://os.intochat.io"
                : $"https://os.{resolvedEnv}.intochat.io";

        return new InstallConfig
        {
            Subscription = resolvedSubscription,
            Location = resolvedLocation,
            Prefix = resolvedPrefix,
            Env = resolvedEnv,
            ResourceGroup = resourceGroup,
            RegistryServer = registryServer,
            OsImage = $"{registryServer}/os:latest",
            CloudImage = includeCloudApp ? $"{registryServer}/cloud:latest" : "",
            SandboxImage = includeSandboxApp ? $"{registryServer}/sandbox:latest" : "",
            DevSlotJob = includeDevSlotJob
                ? new DevSlotJobConfig
                {
                    Enabled = true,
                    Image = $"{registryServer}/devslot:latest",
                }
                : new DevSlotJobConfig(),
            Registry = new RegistryConfig
            {
                Enabled = true,
                Sku = "Basic",
            },
            Postgres = new PostgresConfig
            {
                Enabled = true,
                Sku = "Standard_B1ms",
                Tier = "Burstable",
                StorageGb = 32,
                AdminLogin = resolvedPrefix,
                Databases = [resolvedPrefix],
            },
            Modules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["auth"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Posture"] = "Secured",
                    ["PublicBaseUrl"] = resolvedPublicUrl,
                },
            },
        }.Validated();
    }

    private static string? TryResolveAzCliSubscription()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (OperatingSystem.IsWindows())
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("az account show --query id -o tsv");
            }
            else
            {
                psi.ArgumentList.Add("account");
                psi.ArgumentList.Add("show");
                psi.ArgumentList.Add("--query");
                psi.ArgumentList.Add("id");
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("tsv");
            }
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) { return null; }
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            return process.ExitCode == 0 && Guid.TryParse(output, out _) ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public string GroupName => ResourceGroup.Length > 0 ? ResourceGroup : ResourceNames.ResourceGroup(Prefix, Env);

    public string IdentityName => Named("identity", ResourceNames.Identity(Prefix, Env));

    public string LogsName => Named("logs", ResourceNames.LogWorkspace(Prefix, Env));

    public string StorageName => Named("storage", ResourceNames.StorageAccount(Prefix, Env));

    public string VaultName => Named("vault", ResourceNames.KeyVault(Prefix, Env));

    public string EnvironmentName => Named("environment", ResourceNames.Environment(Prefix, Env));

    public string OsName => Named("os", ResourceNames.App(Prefix, Env, "os"));

    public string CloudName => Named("cloud", ResourceNames.App(Prefix, Env, "cloud"));

    public string SandboxName => Named("sandbox", ResourceNames.App(Prefix, Env, "sandbox"));

    public string DevSlotJobName => Named("devslot", ResourceNames.App(Prefix, Env, "devslot"));

    public string DevSlotIdentityName => Named("devslot-identity", ResourceNames.For(Prefix, Env, "devslot-id"));

    // names.registry, else the first label of registryServer (an install config that already names its registry), else derived.
    public string RegistryName => Named("registry", RegistryServer.Length > 0 ? RegistryServer.Split('.')[0] : ResourceNames.Registry(Prefix, Env));

    public string PostgresName => Named("postgres", ResourceNames.Postgres(Prefix, Env));

    public string FoundryName => Named("foundry", ResourceNames.Foundry(Prefix, Env));

    // The login server of the registry: the config's registryServer when given, else <registry name>.azurecr.io when one is created.
    public string LoginServer => RegistryServer.Length > 0 ? RegistryServer : Registry.Enabled ? RegistryName + ".azurecr.io" : "";

    private string Named(string key, string derived)
    {
        var entry = Names.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(entry.Value) ? derived : entry.Value;
    }

    public InstallConfig WithManifest(ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var modules = new Dictionary<string, Dictionary<string, string>>(Modules, StringComparer.OrdinalIgnoreCase);
        return this with
        {
            OsImage = !string.IsNullOrWhiteSpace(manifest.OsImage) ? RewriteRegistry(manifest.OsImage, LoginServer) : OsImage,
            CloudImage = !string.IsNullOrWhiteSpace(manifest.CloudImage) ? RewriteRegistry(manifest.CloudImage, LoginServer) : CloudImage,
            SandboxImage = !string.IsNullOrWhiteSpace(manifest.SandboxImage) ? RewriteRegistry(manifest.SandboxImage, LoginServer) : SandboxImage,
            DevSlotJob = DevSlotJob.Enabled && !string.IsNullOrWhiteSpace(manifest.DevSlotImage)
                ? DevSlotJob with { Image = RewriteRegistry(manifest.DevSlotImage, LoginServer) }
                : DevSlotJob,
            Modules = modules,
        };
    }

    private static string RewriteRegistry(string image, string loginServer)
    {
        if (string.IsNullOrWhiteSpace(loginServer)) { return image; }
        var slash = image.IndexOf('/');
        return slash > 0 && image[..slash].Contains('.', StringComparison.Ordinal)
            ? $"{loginServer}{image[slash..]}"
            : image;
    }

    public ModuleSettings SettingsFor(string module)
    {
        var entry = Modules.FirstOrDefault(pair => string.Equals(pair.Key, module, StringComparison.OrdinalIgnoreCase));
        return entry.Value is null ? ModuleSettings.Empty : new ModuleSettings(entry.Value);
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
        if (Postgres.Enabled && (Postgres.StorageGb <= 0 || Postgres.AdminLogin.Length == 0))
        {
            throw new InvalidOperationException("The postgres config needs a positive storageGb and an adminLogin.");
        }
        if (Foundry.Enabled)
        {
            ValidateFoundryIdentifier("names.foundry", FoundryName, required: true);
            ValidateFoundryIdentifier("foundry.sku", Foundry.Sku, required: true);
            foreach (var deployment in Foundry.Deployments)
            {
                ValidateFoundryIdentifier("foundry.deployments[].name", deployment.Name, required: true);
                ValidateFoundryIdentifier("foundry.deployments[].modelName", deployment.ModelName, required: true);
                ValidateFoundryIdentifier("foundry.deployments[].modelFormat", deployment.ModelFormat, required: true);
                ValidateFoundryIdentifier("foundry.deployments[].modelVersion", deployment.ModelVersion, required: false);
                ValidateFoundryIdentifier("foundry.deployments[].skuName", deployment.SkuName, required: true);
                if (deployment.Capacity <= 0)
                {
                    throw new InvalidOperationException("Each foundry deployment needs a name, modelName, and positive capacity.");
                }
            }
        }
        if (LogWorkspace.DailyQuotaGb <= 0)
        {
            throw new InvalidOperationException("The logWorkspace config needs a positive dailyQuotaGb.");
        }
        if (CostGuardrails.Enabled)
        {
            if (CostGuardrails.MonthlyBudgetEur <= 0)
            {
                throw new InvalidOperationException("The costGuardrails config needs a positive monthlyBudgetEur.");
            }
            if (string.IsNullOrWhiteSpace(CostGuardrails.AlertEmail))
            {
                throw new InvalidOperationException("The costGuardrails config needs a non-empty alertEmail.");
            }
        }
        var normalizedDevSlotJob = DevSlotJob;
        if (DevSlotJob.Enabled)
        {
            var resolvedImage = !string.IsNullOrWhiteSpace(DevSlotJob.Image)
                ? DevSlotJob.Image.Trim()
                : LoginServer.Length > 0
                    ? $"{LoginServer}/devslot:latest"
                    : "";
            if (string.IsNullOrWhiteSpace(resolvedImage))
            {
                throw new InvalidOperationException("The devSlotJob config needs a non-empty 'image' (or a configured registryServer/registry).");
            }
            if (double.IsNaN(DevSlotJob.Cpu) || double.IsInfinity(DevSlotJob.Cpu) || DevSlotJob.Cpu <= 0 || DevSlotJob.Cpu > DevSlotJobConfig.MaxCpu)
            {
                throw new InvalidOperationException($"The devSlotJob config requires 0 < cpu <= {DevSlotJobConfig.MaxCpu} (Policy Rule B).");
            }
            if (!DevSlotJobConfig.IsAllowedMemory(DevSlotJob.Memory))
            {
                throw new InvalidOperationException($"The devSlotJob config requires memory <= {DevSlotJobConfig.MaxMemoryGi}Gi using a valid ACA Consumption tier (Policy Rule B).");
            }
            if (DevSlotJob.Parallelism < 1 || DevSlotJob.Parallelism > DevSlotJobConfig.MaxParallelism)
            {
                throw new InvalidOperationException($"The devSlotJob config requires 1 <= parallelism <= {DevSlotJobConfig.MaxParallelism} (Policy Rule B).");
            }
            if (DevSlotJob.ReplicaTimeoutSeconds < 1 || DevSlotJob.ReplicaTimeoutSeconds > DevSlotJobConfig.MaxReplicaTimeoutSeconds)
            {
                throw new InvalidOperationException($"The devSlotJob config requires 1 <= replicaTimeoutSeconds <= {DevSlotJobConfig.MaxReplicaTimeoutSeconds} (Policy Rule B).");
            }
            if (DevSlotJob.ReplicaRetryLimit != DevSlotJobConfig.RequiredReplicaRetryLimit)
            {
                throw new InvalidOperationException($"The devSlotJob config requires replicaRetryLimit == {DevSlotJobConfig.RequiredReplicaRetryLimit} (Policy Rule B).");
            }
            normalizedDevSlotJob = DevSlotJob with { Image = resolvedImage, Memory = DevSlotJob.Memory.Trim() };
        }
        return this with
        {
            ClusterId = ClusterId.Length > 0 ? ClusterId : "intochat-" + Env,
            ServiceId = ServiceId.Length > 0 ? ServiceId : "intochat-" + Env,
            Postgres = Postgres with { AdminLogin = Postgres.AdminLogin.Length > 0 ? Postgres.AdminLogin : "dbadmin" },
            CostGuardrails = CostGuardrails with
            {
                ProtectedResourceGroups = NormalizeProtectedResourceGroups(CostGuardrails.ProtectedResourceGroups),
            },
            DevSlotJob = normalizedDevSlotJob,
        };
    }

    private static string[] NormalizeProtectedResourceGroups(string[]? configured)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Hard owner invariant: LeedGenerator and sv are ALWAYS protected, preserving exact casing.
        foreach (var required in CostGuardrailConfig.DefaultProtectedResourceGroups)
        {
            if (seen.Add(required))
            {
                result.Add(required);
            }
        }

        if (configured is not null)
        {
            foreach (var rg in configured)
            {
                var trimmed = rg?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }
        }

        return [.. result];
    }

    private static void ValidateFoundryIdentifier(string field, string value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw new InvalidOperationException($"The foundry config needs a non-empty '{field}'.");
            }
            return;
        }

        if (!FoundryIdentifierPattern().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"The foundry config field '{field}' contains invalid characters; only letters, digits, '.', '_' and '-' (1-128 chars) are allowed.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FoundryIdentifierPattern();
}

// registry: { "enabled": true, "sku": "Basic" }. Enabled by default.
public sealed record RegistryConfig
{
    public bool Enabled { get; init; } = true;
    public string Sku { get; init; } = "Basic";
}

// postgres: { "enabled": true, "sku": "Standard_B1ms", "tier": "Burstable", "storageGb": 32, "adminLogin": "dbadmin", "databases": ["intochat"] }.
// Disabled by default. The first database is written to the canonical IntegrationSchema Key Vault secret postgres-connectionstring.
public sealed record PostgresConfig
{
    public bool Enabled { get; init; }
    public string Sku { get; init; } = "Standard_B1ms";
    public string Tier { get; init; } = "Burstable";
    public int StorageGb { get; init; } = 32;
    public string AdminLogin { get; init; } = "dbadmin";
    public string[] Databases { get; init; } = [];
}

// foundry: { "enabled": true, "sku": "S0", "deployments": [ { "name": "DeepSeek-V3.2", "modelName": "DeepSeek-V3.2", "modelFormat": "DeepSeek" } ] }.
// Disabled by default. When enabled, provisions a shared Azure AI Foundry (AIServices) resource with RBAC to the foundation Managed Identity.
public sealed record FoundryConfig
{
    public bool Enabled { get; init; }
    public string Sku { get; init; } = "S0";
    public FoundryDeploymentConfig[] Deployments { get; init; } = [];
}

public sealed record FoundryDeploymentConfig
{
    public string Name { get; init; } = "";
    public string ModelName { get; init; } = "";
    public string ModelFormat { get; init; } = "OpenAI";
    public string ModelVersion { get; init; } = "";
    public string SkuName { get; init; } = "GlobalStandard";
    public int Capacity { get; init; } = 10;
}

// logWorkspace: { "dailyQuotaGb": 0.2 }. Caps Log Analytics daily ingestion (default 0.2 GB/day).
public sealed record LogWorkspaceConfig
{
    public double DailyQuotaGb { get; init; } = 0.2;
}

// costGuardrails: { "enabled": true, "monthlyBudgetEur": 185.0, "alertEmail": "vlad@intochat.io", "protectedResourceGroups": ["LeedGenerator", "sv", "NetworkWatcherRG"] }.
// Enabled by default even when omitted in JSON. Enforces Layer 1 Azure Policy Deny rules and Layer 2 automated budget circuit-breaker.
public sealed record CostGuardrailConfig
{
    public static readonly string[] DefaultProtectedResourceGroups = ["LeedGenerator", "sv", "NetworkWatcherRG"];

    public bool Enabled { get; init; } = true;
    public double MonthlyBudgetEur { get; init; } = 185.0;
    public string AlertEmail { get; init; } = "vlad@intochat.io";
    public string[] ProtectedResourceGroups { get; init; } = DefaultProtectedResourceGroups;
}

// devSlotJob: { "enabled": true, "image": "acrintochattest.azurecr.io/devslot:latest", "cpu": 2.0, "memory": "4Gi", "parallelism": 1, "replicaTimeoutSeconds": 900 }.
// Disabled by default for full backward compatibility. Enforces Policy Rule B ceilings at validation time.
public sealed record DevSlotJobConfig
{
    public const double MaxCpu = 2.0;
    public const double MaxMemoryGi = 4.0;
    public const int MaxParallelism = 2;
    public const int MaxReplicaTimeoutSeconds = 900;
    public const int RequiredReplicaRetryLimit = 0;

    public static readonly string[] AllowedMemoryTiers = ["0.5Gi", "1Gi", "1.5Gi", "2Gi", "2.5Gi", "3Gi", "3.5Gi", "4Gi"];

    public bool Enabled { get; init; }
    public string Image { get; init; } = "";
    public double Cpu { get; init; } = 2.0;
    public string Memory { get; init; } = "4Gi";
    public int Parallelism { get; init; } = 1;
    public int ReplicaTimeoutSeconds { get; init; } = MaxReplicaTimeoutSeconds;
    public int ReplicaRetryLimit { get; init; } = RequiredReplicaRetryLimit;

    public static bool IsAllowedMemory(string? memory)
        => !string.IsNullOrWhiteSpace(memory)
            && AllowedMemoryTiers.Contains(memory.Trim(), StringComparer.Ordinal);
}
