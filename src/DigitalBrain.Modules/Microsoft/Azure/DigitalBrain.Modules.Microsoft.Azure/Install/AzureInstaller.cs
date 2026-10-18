using System.Text.Json;
using DigitalBrain.Framework.Modules;
using global::Azure;

namespace DigitalBrain.Microsoft.Azure.Install;

// dotnet run --project DigitalBrain.OS -- install --env test --config deployment/test.json [--dry-run] [--secrets-from file]
// Runs before any cluster exists: the specs are ensured straight against ARM, in dependency order, and the
// deployments of the composed modules run after the foundation.
public static class AzureInstaller
{
    public const string MasterKeyVariable = "DIGITALBRAIN_MASTER_KEY";

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

        var masterKey = "";
        if (!dryRun)
        {
            try { masterKey = await MasterKey(secretsFrom, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
            {
                await error.WriteLineAsync($"install: {ex.Message}");
                return 2;
            }
        }

        var current = "";
        async Task<ResourceRef> Ensure(ResourceSpec spec, CancellationToken token)
        {
            current = InstallPlan.DryRunLine(spec);
            await output.WriteLineAsync(current);
            if (dryRun) { return new ResourceRef(ResourceIds.For(config.Subscription, spec), spec.Kind, spec.Name, spec.ResourceGroup); }
            return await arm.Ensure(config.Subscription, AzureTags.Tagged(spec), token);
        }

        try
        {
            // The foundation first, then each composed module's deployment, then the OS app with what they asked for.
            foreach (var spec in InstallPlan.Foundation(config))
            {
                var send = spec is SecretSpec secret && secret.Name == InstallPlan.MasterKeySecret ? secret with { Value = masterKey } : spec;
                await Ensure(send, ct);
            }
            var target = new DeploymentTarget(config.Subscription, config.Location, config.Prefix, config.Env);
            var foundation = InstallPlan.Refs(config);
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            var secrets = new List<SecretRef>();
            foreach (var (module, deployment) in deployments)
            {
                var context = new ModuleDeploymentContext(target, foundation, module, config.SettingsFor(module), Ensure, ct);
                current = $"deploy module {module}";
                await deployment.Deploy(context);
                foreach (var (name, value) in context.RuntimeVariables) { variables[name] = value; }
                secrets.AddRange(context.RuntimeSecrets);
            }
            await Ensure(InstallPlan.OsApp(config, variables, secrets), ct);
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            await error.WriteLineAsync($"install: {current} failed: {ex.Message}");
            return 1;
        }
        return 0;
    }

    private static async Task<string> MasterKey(string? secretsFrom, CancellationToken ct)
    {
        if (secretsFrom is not null)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(secretsFrom, ct)) ?? [];
            if (values.TryGetValue(InstallPlan.MasterKeySecret, out var fromFile) && fromFile.Length > 0) { return fromFile; }
        }
        var fromEnvironment = Environment.GetEnvironmentVariable(MasterKeyVariable);
        return string.IsNullOrEmpty(fromEnvironment)
            ? throw new InvalidOperationException($"The master key is needed: set {MasterKeyVariable} or pass --secrets-from with a '{InstallPlan.MasterKeySecret}' entry.")
            : fromEnvironment;
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
