using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Kernel.Install;

// dotnet run --project IntoChat.OS.Kernel -- install --env test --config deployment/test.json [--manifest release-manifest.json] [--promote-from <id>] [--dry-run] [--print-env] [--secrets-from file]
// Runs before any cluster exists: the specs are ensured straight against ARM, in dependency order, and the
// deployments of the composed modules run after the foundation.
public static class AzureInstaller
{
    public const string MasterKeyVariable = "INTOCHATOS_MASTER_KEY";
    public const string DeploySecretsJsonVariable = "OS_DEPLOY_SECRETS_JSON";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool IsInstall(string[] args) => args is [var verb, ..] && verb is "install" or "pulumi";

    public static Task<int> Run(string[] args, IAzureArm arm, IEnumerable<OsModule> modules, TextWriter output, TextWriter error, CancellationToken ct) =>
        Run(args, arm, modules, output, error, null, ct);

    public static async Task<int> Run(string[] args, IAzureArm arm, IEnumerable<OsModule> modules, TextWriter output, TextWriter error, HttpClient? httpClient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        InstallConfig config;
        bool dryRun, printEnv, emitPulumi;
        string? secretsFrom, manifestPath, promoteFrom, pulumiOut;
        ReleaseManifest? manifest = null;
        try
        {
            var options = Options.Parse(args);
            (dryRun, secretsFrom, printEnv, manifestPath, promoteFrom, emitPulumi, pulumiOut) =
                (options.DryRun, options.SecretsFrom, options.PrintEnv, options.Manifest, options.PromoteFrom, options.EmitPulumi, options.PulumiOut);
            config = InstallConfig.Parse(await File.ReadAllTextAsync(options.Config, ct));
            if (!string.Equals(config.Env, options.Env, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"--env '{options.Env}' does not match the config's env '{config.Env}'.");
            }

            if (!string.IsNullOrEmpty(manifestPath))
            {
                var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
                manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Failed to deserialize release manifest from '{manifestPath}'.");
                if (string.IsNullOrWhiteSpace(manifest.Version)
                    || string.IsNullOrWhiteSpace(manifest.Commit)
                    || string.IsNullOrWhiteSpace(manifest.OsImage)
                    || !manifest.OsImage.Contains("@sha256:", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Release manifest must specify version, commit, and @sha256: image digests.");
                }
                config = config.WithManifest(manifest);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException)
        {
            await error.WriteLineAsync($"install: {ex.Message}", ct);
            return 2;
        }

        var manifests = ManifestEnvironment.ManifestsOf(modules);
        if (emitPulumi)
        {
            try
            {
                var secretsDict = !dryRun ? await SecretValues(secretsFrom, ct) : new Dictionary<string, string>(StringComparer.Ordinal);
                var stackPlan = PulumiStackSynthesizer.Synthesize(config, modules, secretsDict);
                if (!string.IsNullOrWhiteSpace(pulumiOut))
                {
                    await PulumiStackSynthesizer.WriteWorkspaceAsync(stackPlan, pulumiOut, ct);
                    var importJson = PulumiStackSynthesizer.ExportImportEntries(stackPlan).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(Path.Combine(pulumiOut, $"import.{config.Env}.json"), importJson + "\n", ct);
                    await output.WriteLineAsync($"Synthesized Pulumi workspace in '{pulumiOut}' ({stackPlan.Resources.Count} resources, {stackPlan.Secrets.Count} secrets, {stackPlan.Variables.Count} module settings, 0 SessionPools).", ct);
                }
                else
                {
                    await output.WriteAsync(stackPlan.PulumiYaml, ct);
                }
                return 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or ArgumentException)
            {
                await error.WriteLineAsync($"pulumi: {ex.Message}", ct);
                return 2;
            }
        }
        if (printEnv)
        {
            // R4-03: the names the OS app will carry, rendered from the profile's foundation and the composed manifests. No Azure call.
            try
            {
                foreach (var name in ManifestEnvironment.EnvironmentNames(config, manifests)) { await output.WriteLineAsync(name, ct); }
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                await error.WriteLineAsync($"install: {ex.Message}", ct);
                return 2;
            }
        }

        IReadOnlyList<ModuleDeploymentBinding> deployments;
        try { deployments = ModuleDeployments.Discover(modules); }
        catch (InvalidOperationException ex)
        {
            await error.WriteLineAsync($"install: {ex.Message}", ct);
            return 2;
        }

        var provided = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!dryRun)
        {
            try { provided = await SecretValues(secretsFrom, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
            {
                await error.WriteLineAsync($"install: {ex.Message}", ct);
                return 2;
            }
        }

        var current = "";
        var planned = new List<ResourceSpec>();
        async Task<ResourceRef> Ensure(ResourceSpec spec, CancellationToken token)
        {
            current = InstallPlan.DryRunLine(spec);
            await output.WriteLineAsync(current, token);
            planned.Add(spec);
            if (dryRun) { return new ResourceRef(ResourceIds.For(config.Subscription, spec), spec.Kind, spec.Name, spec.ResourceGroup); }
            return await arm.Ensure(config.Subscription, AzureTags.Tagged(spec), token);
        }

        try
        {
            // Resolve principal ID if not specified in config
            if (string.IsNullOrWhiteSpace(config.InstallerPrincipalId))
            {
                current = "resolve current principal object id";
                var principalId = await arm.CurrentPrincipalObjectId(ct);
                config = config with { InstallerPrincipalId = principalId };
            }

            // The foundation first (creating the resource group and registry), then image promotion if requested,
            // then each composed module's deployment, then the OS app with what they asked for.
            var foundation = InstallPlan.Refs(config);
            foreach (var spec in InstallPlan.Foundation(config))
            {
                var made = await Ensure(dryRun ? spec : WithValue(spec, provided), ct);
                if (spec is RegistrySpec && made.Endpoint.Length > 0)
                {
                    foundation = foundation with { RegistryServer = made.Endpoint, Registry = made };
                }
            }

            if (!string.IsNullOrEmpty(promoteFrom) && manifest is not null)
            {
                current = $"promote images from {promoteFrom}";
                var targetGroup = config.GroupName;
                var targetRegistry = config.RegistryName;
                await arm.ImportImage(config.Subscription, targetGroup, targetRegistry, promoteFrom, manifest.OsImage, ct);
            }

            var target = new DeploymentTarget(config.Subscription, config.Location, config.Prefix, config.Env);
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            var secrets = new List<KeyVaultSecretRef>();
            foreach (var (module, deployment) in deployments)
            {
                var context = new ModuleDeploymentContext(target, foundation, module, config.SettingsFor(module), Ensure, ct, provided, dryRun,
                    (vault, name, token) => arm.ReadSecret(vault, name, token));
                current = $"deploy module {module}";
                await deployment.Deploy(context);
                foreach (var (name, value) in context.RuntimeVariables) { variables[name] = value; }
                secrets.AddRange(context.RuntimeSecrets);
            }
            // The manifests' own settings (values the deployment input supplies) and secrets (Key Vault references).
            var (settingVariables, manifestSecrets) = ManifestEnvironment.Render(config, manifests);
            foreach (var (name, value) in settingVariables) { variables[name] = value; }
            foreach (var secret in manifestSecrets)
            {
                // A reference to a vault secret that does not exist stops the container app from starting: fail here, before it.
                if (!dryRun && await arm.ReadSecret(secret.Vault, secret.VaultSecret, ct) is null)
                {
                    throw new InvalidOperationException($"the Key Vault secret '{secret.VaultSecret}' of a composed module is not in {secret.Vault}; store it before installing.");
                }
                secrets.Add(secret);
            }
            // IntegrationSchema secrets (IntoChatOS__Integrations__{id}__{field}) are optional at install time: when present in
            // --secrets-from or already stored in Key Vault, bind them to the container app so redeploys preserve every integration secret.
            var validated = config.Validated();
            foreach (var secret in ManifestEnvironment.IntegrationSecretsOf(modules, validated.VaultName))
            {
                if (secrets.Any(existing => string.Equals(existing.Variable, secret.Variable, StringComparison.Ordinal))) { continue; }
                if (!dryRun && provided.TryGetValue(secret.VaultSecret, out var secretValue) && !string.IsNullOrEmpty(secretValue))
                {
                    await Ensure(new SecretSpec
                    {
                        Name = secret.VaultSecret,
                        ResourceGroup = validated.ResourceGroup,
                        Location = validated.Location,
                        Vault = secret.Vault,
                        Value = secretValue,
                        Overwrite = true,
                    }, ct);
                    secrets.Add(secret);
                }
                else if (!dryRun && await arm.ReadSecret(secret.Vault, secret.VaultSecret, ct) is not null)
                {
                    secrets.Add(secret);
                }
            }
            var osApp = InstallPlan.OsApp(config, variables, secrets);
            var osMade = await Ensure(osApp with { RegistryServer = foundation.RegistryServer }, ct);

            if (dryRun)
            {
                await output.WriteLineAsync(InstallPlan.Summary(planned), ct);
            }
            else if (arm is not (IntoChat.OS.Kernel.InMemoryCloud or IntoChat.OS.Kernel.AspireCloud) || httpClient is not null)
            {
                var osHost = HostOnly(osMade.Endpoint);
                if (!string.IsNullOrWhiteSpace(osHost))
                {
                    current = "smoke verification";
                    using var client = httpClient is not null ? null : new HttpClient();
                    var maxAttempts = httpClient is null ? 18 : 1;
                    var retryDelay = httpClient is null ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
                    await VerifySmokeAsync(httpClient ?? client!, osHost, maxAttempts, retryDelay, ct);
                }
            }
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            OsConsoleLog.For(typeof(AzureInstaller)).LogSwallowed(ex, $"Azure install step '{current}' failed.");
            await error.WriteLineAsync($"install: {current} failed: {ex.Message}", ct);
            return 1;
        }
        return 0;
    }

    public static Task VerifySmokeAsync(HttpClient client, string osHost, CancellationToken ct) =>
        VerifySmokeAsync(client, osHost, maxAttempts: 1, retryDelay: TimeSpan.Zero, ct);

    internal static async Task VerifySmokeAsync(HttpClient client, string osHost, int maxAttempts, TimeSpan retryDelay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(osHost))
        {
            throw new InvalidOperationException("OS endpoint host is required for smoke verification.");
        }

        await ProbeEndpointAsync(client, new Uri($"https://{osHost}/ready"), "OS ready check", maxAttempts, retryDelay, ct);
        await ProbeEndpointAsync(client, new Uri($"https://{osHost}/"), "UI root check", maxAttempts, retryDelay, ct);
    }

    private static async Task ProbeEndpointAsync(HttpClient client, Uri uri, string label, int maxAttempts, TimeSpan retryDelay, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var response = await client.GetAsync(uri, attemptCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
                lastError = new InvalidOperationException($"{label} {uri} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
            {
                lastError = new InvalidOperationException($"{label} {uri} timed out: {oce.Message}", oce);
            }
            catch (HttpRequestException hre)
            {
                lastError = hre;
            }

            if (attempt < maxAttempts && retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(retryDelay, ct);
            }
        }

        throw lastError ?? new InvalidOperationException($"{label} {uri} failed.");
    }

    private static string HostOnly(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "";
        }
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(endpoint).Host;
        }
        var slash = endpoint.IndexOf('/');
        return slash >= 0 ? endpoint[..slash] : endpoint;
    }

    // The master key comes from the file or the environment when given; otherwise the vault generates it once and keeps it. The Postgres admin password is generated once
    // when neither has it (the spec is Overwrite=false, so a later install keeps the stored one); the GHCR token is optional.
    private static async Task<Dictionary<string, string>> SecretValues(string? secretsFrom, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var envSecretsJson = Environment.GetEnvironmentVariable(DeploySecretsJsonVariable);
        if (!string.IsNullOrWhiteSpace(envSecretsJson))
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(envSecretsJson, JsonOptions);
            if (parsed is not null)
            {
                foreach (var (name, value) in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(value)) { values[name] = value; }
                }
            }
        }

        if (secretsFrom is not null)
        {
            var fileJson = await File.ReadAllTextAsync(secretsFrom, ct);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(fileJson, JsonOptions);
            if (parsed is not null)
            {
                foreach (var (name, value) in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(value)) { values[name] = value; }
                }
            }
        }

        if (!values.ContainsKey(InstallPlan.MasterKeySecret))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(MasterKeyVariable);
            if (!string.IsNullOrEmpty(fromEnvironment)) { values[InstallPlan.MasterKeySecret] = fromEnvironment; }
        }
        if (!values.ContainsKey(InstallPlan.PostgresPasswordSecret))
        {
            values[InstallPlan.PostgresPasswordSecret] = GeneratedPassword();
        }
        return values;
    }

    private static ResourceSpec WithValue(ResourceSpec spec, Dictionary<string, string> provided) => spec switch
    {
        SecretSpec { Name: InstallPlan.GhcrTokenSecret } secret => provided.TryGetValue(secret.Name, out var token)
            ? secret with { Value = token }
            : secret with { Overwrite = false },
        SecretSpec { Name: InstallPlan.MasterKeySecret, Value.Length: 0 } secret when !provided.ContainsKey(secret.Name) =>
            secret with { Generator = SecretGenerators.Base64(32) },
        SecretSpec { Value.Length: 0 } secret when provided.TryGetValue(secret.Name, out var value) => secret with { Value = value },
        _ => spec,
    };

    // 32 letters and digits: valid for Postgres and safe inside a connection string.
    private static string GeneratedPassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(32, alphabet, static (span, letters) =>
        {
            for (var i = 0; i < span.Length; i++) { span[i] = letters[RandomNumberGenerator.GetInt32(letters.Length)]; }
        });
    }

    private sealed record Options(string Env, string Config, bool DryRun, string? SecretsFrom, bool PrintEnv, string? Manifest, string? PromoteFrom, bool EmitPulumi, string? PulumiOut)
    {
        public static Options Parse(string[] args)
        {
            string? env = null, config = null, secrets = null, manifest = null, promoteFrom = null, pulumiOut = null;
            var dryRun = false;
            var printEnv = false;
            var emitPulumi = args is ["pulumi", ..];
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--env": env = Value(args, ref i); break;
                    case "--config": config = Value(args, ref i); break;
                    case "--secrets-from": secrets = Value(args, ref i); break;
                    case "--manifest": manifest = Value(args, ref i); break;
                    case "--promote-from": promoteFrom = Value(args, ref i); break;
                    case "--pulumi-out" or "--output" or "-o": pulumiOut = Value(args, ref i); emitPulumi = true; break;
                    case "--emit-pulumi": emitPulumi = true; break;
                    case "--dry-run": dryRun = true; break;
                    case "--print-env": printEnv = true; break;
                    default: throw new ArgumentException($"Unknown install argument '{args[i]}'.");
                }
            }
            return new Options(
                env ?? throw new ArgumentException("install needs --env."),
                config ?? throw new ArgumentException("install needs --config."),
                dryRun,
                secrets,
                printEnv,
                manifest,
                promoteFrom,
                emitPulumi,
                pulumiOut);
        }

        private static string Value(string[] args, ref int i) =>
            ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
    }
}
