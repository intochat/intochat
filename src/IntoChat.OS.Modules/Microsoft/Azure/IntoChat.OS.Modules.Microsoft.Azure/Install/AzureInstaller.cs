using System.Security.Cryptography;
using System.Text.Json;
using IntoChat.OS.Framework.Modules;
using global::Azure;

namespace IntoChat.OS.Microsoft.Azure.Install;

// dotnet run --project IntoChat.OS.Kernel -- install --env test --config deployment/test.json [--dry-run] [--secrets-from file]
// Runs before any cluster exists: the specs are ensured straight against ARM, in dependency order, and the
// deployments of the composed modules run after the foundation.
public static class AzureInstaller
{
    public const string MasterKeyVariable = "INTOCHATOS_MASTER_KEY";

    public static bool IsInstall(string[] args) => args is [var verb, ..] && verb == "install";

    public static async Task<int> Run(string[] args, IAzureArm arm, IEnumerable<Module> modules, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        InstallConfig config;
        bool dryRun;
        string? secretsFrom;
        try
        {
            var options = Options.Parse(args);
            (dryRun, secretsFrom) = (options.DryRun, options.SecretsFrom);
            config = InstallConfig.Parse(await File.ReadAllTextAsync(options.Config, ct));
            if (!string.Equals(config.Env, options.Env, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"--env '{options.Env}' does not match the config's env '{config.Env}'.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException)
        {
            await error.WriteLineAsync($"install: {ex.Message}");
            return 2;
        }

        IReadOnlyList<ModuleDeploymentBinding> deployments;
        try { deployments = ModuleDeployments.Discover(modules); }
        catch (InvalidOperationException ex)
        {
            await error.WriteLineAsync($"install: {ex.Message}");
            return 2;
        }

        var provided = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!dryRun)
        {
            try { provided = await SecretValues(secretsFrom, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
            {
                await error.WriteLineAsync($"install: {ex.Message}");
                return 2;
            }
        }

        var current = "";
        var planned = new List<ResourceSpec>();
        async Task<ResourceRef> Ensure(ResourceSpec spec, CancellationToken token)
        {
            current = InstallPlan.DryRunLine(spec);
            await output.WriteLineAsync(current);
            planned.Add(spec);
            if (dryRun) { return new ResourceRef(ResourceIds.For(config.Subscription, spec), spec.Kind, spec.Name, spec.ResourceGroup); }
            return await arm.Ensure(config.Subscription, AzureTags.Tagged(spec), token);
        }

        try
        {
            // The foundation first, then each composed module's deployment, then the OS app with what they asked for.
            var foundation = InstallPlan.Refs(config);
            foreach (var spec in InstallPlan.Foundation(config))
            {
                var made = await Ensure(dryRun ? spec : WithValue(spec, provided), ct);
                if (spec is RegistrySpec && made.Endpoint.Length > 0)
                {
                    foundation = foundation with { RegistryServer = made.Endpoint, Registry = made };
                }
            }
            var target = new DeploymentTarget(config.Subscription, config.Location, config.Prefix, config.Env);
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            var secrets = new List<SecretRef>();
            foreach (var (module, deployment) in deployments)
            {
                var context = new ModuleDeploymentContext(target, foundation, module, config.SettingsFor(module), Ensure, ct, provided, dryRun);
                current = $"deploy module {module}";
                await deployment.Deploy(context);
                foreach (var (name, value) in context.RuntimeVariables) { variables[name] = value; }
                secrets.AddRange(context.RuntimeSecrets);
            }
            var osApp = InstallPlan.OsApp(config, variables, secrets);
            await Ensure(osApp with { RegistryServer = foundation.RegistryServer }, ct);
            if (dryRun) { await output.WriteLineAsync(InstallPlan.Summary(planned)); }
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            await error.WriteLineAsync($"install: {current} failed: {ex.Message}");
            return 1;
        }
        return 0;
    }

    // The master key must come from the file or the environment; the Postgres admin password is generated once
    // when neither has it (the spec is Overwrite=false, so a later install keeps the stored one); the GHCR token is optional.
    private static async Task<Dictionary<string, string>> SecretValues(string? secretsFrom, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (secretsFrom is not null)
        {
            foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(secretsFrom, ct)) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(value)) { values[name] = value; }
            }
        }
        if (!values.ContainsKey(InstallPlan.MasterKeySecret))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(MasterKeyVariable);
            values[InstallPlan.MasterKeySecret] = string.IsNullOrEmpty(fromEnvironment)
                ? throw new InvalidOperationException($"The master key is needed: set {MasterKeyVariable} or pass --secrets-from with a '{InstallPlan.MasterKeySecret}' entry.")
                : fromEnvironment;
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

    private sealed record Options(string Env, string Config, bool DryRun, string? SecretsFrom)
    {
        public static Options Parse(string[] args)
        {
            string? env = null, config = null, secrets = null;
            var dryRun = false;
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--env": env = Value(args, ref i); break;
                    case "--config": config = Value(args, ref i); break;
                    case "--secrets-from": secrets = Value(args, ref i); break;
                    case "--dry-run": dryRun = true; break;
                    default: throw new ArgumentException($"Unknown install argument '{args[i]}'.");
                }
            }
            return new Options(
                env ?? throw new ArgumentException("install needs --env."),
                config ?? throw new ArgumentException("install needs --config."),
                dryRun,
                secrets);
        }

        private static string Value(string[] args, ref int i) =>
            ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
    }
}
