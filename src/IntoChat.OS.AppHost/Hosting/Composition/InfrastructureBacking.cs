using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.ArtCraft.Aspire.Hosting;
using IntoChat.OS.Modules.ClickHouse;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Modules.Postgres;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Declarative backing placement for an infrastructure resource need declared by a module manifest.
/// </summary>
public enum ResourceBacking
{
    /// <summary>Do not provision or bind this backing resource.</summary>
    Off,

    /// <summary>Provision a local container resource (e.g. Postgres, ClickHouse, ArtCraft, Sandbox) via Aspire.</summary>
    LocalContainer,

    /// <summary>Provision a local host/process-managed backing resource.</summary>
    LocalProcess,

    /// <summary>Bind to an external/cloud-managed backing resource via configuration/parameters.</summary>
    Cloud,
}

/// <summary>
/// Fluent builder for <see cref="DeploymentChain.WithInfrastructure"/> that binds backing policies
/// to infrastructure needs without fake in-process module composition calls.
/// </summary>
public sealed class InfrastructureBackingBuilder
{
    private readonly Dictionary<string, (ResourceBacking Backing, string? DatabaseName)> _policies = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyDictionary<string, (ResourceBacking Backing, string? DatabaseName)> Policies => _policies;

    public InfrastructureBackingBuilder Postgres(ResourceBacking backing = ResourceBacking.LocalContainer, string? databaseName = null)
    {
        EnsureNotLocalProcess(backing, nameof(Postgres));
        if (databaseName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        }
        _policies["postgres"] = (backing, databaseName?.Trim());
        return this;
    }

    public InfrastructureBackingBuilder ClickHouse(ResourceBacking backing = ResourceBacking.LocalContainer, string? databaseName = null)
    {
        EnsureNotLocalProcess(backing, nameof(ClickHouse));
        if (databaseName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        }
        _policies["clickhouse"] = (backing, databaseName?.Trim());
        return this;
    }

    public InfrastructureBackingBuilder ArtCraft(ResourceBacking backing = ResourceBacking.LocalContainer)
    {
        EnsureNotLocalProcess(backing, nameof(ArtCraft));
        _policies["artcraft"] = (backing, null);
        return this;
    }

    public InfrastructureBackingBuilder Playwright(ResourceBacking backing = ResourceBacking.LocalContainer)
    {
        EnsureNotLocalProcess(backing, nameof(Playwright));
        _policies["playwright"] = (backing, null);
        return this;
    }

    public InfrastructureBackingBuilder Sandbox(ResourceBacking backing = ResourceBacking.LocalContainer)
    {
        EnsureNotLocalProcess(backing, nameof(Sandbox));
        _policies[SandboxModuleHosting.ModuleName] = (backing, null);
        return this;
    }

    /// <summary>
    /// Derives a deterministic PostgreSQL database name (<c>brain_&lt;sha256-16&gt;</c>) from the deployment/brain name,
    /// delegating to <see cref="PostgresModuleOptions.DeriveDatabaseName"/> so AppHost and runtime share one canonical rule.
    /// </summary>
    public static string DerivePostgresDatabaseName(string deploymentName)
        => PostgresModuleOptions.DeriveDatabaseName(deploymentName);

    internal IntoChatOSBuilder ApplyTo(IntoChatOSBuilder os, DeploymentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(profile);

        if (_policies.TryGetValue("postgres", out var pg) && pg.Backing != ResourceBacking.Off && profile.IsEnabled("postgres"))
        {
            var dbName = pg.DatabaseName ?? DerivePostgresDatabaseName(os.ServiceId);
            var options = new PostgresModuleOptions();
            if (pg.Backing == ResourceBacking.LocalContainer)
            {
                options.WithPostgres(o => o.DatabaseName = dbName);
            }
            else
            {
                options.Hosting.Enabled = false;
                options.Hosting.DatabaseName = dbName;
            }
            os = os.WithModule<PostgresModule, PostgresModuleOptions>(options);
        }

        if (_policies.TryGetValue("clickhouse", out var ch) && ch.Backing != ResourceBacking.Off && profile.IsEnabled("clickhouse"))
        {
            var options = new ClickHouseModuleOptions();
            if (!string.IsNullOrWhiteSpace(ch.DatabaseName))
            {
                options.DatabaseName = ch.DatabaseName;
            }
            if (ch.Backing == ResourceBacking.LocalContainer)
            {
                options.WithClickHouse();
            }
            else
            {
                options.Hosting.Enabled = false;
            }
            os = os.WithModule<ClickHouseModule, ClickHouseModuleOptions>(options);
        }

        // Local Docker container hosting is strictly gated on ResourceBacking.LocalContainer.
        // ResourceBacking.Cloud and ResourceBacking.Off never provision local containers or localhost container origins.
        if (_policies.TryGetValue("playwright", out var pw) && pw.Backing == ResourceBacking.LocalContainer && profile.IsEnabled("playwright"))
        {
            os = os.WithModule<PlaywrightModule, PlaywrightModuleOptions>(playwright => playwright.AppOrigins = ArtCraftModuleOptions.HostedOrigins);
        }

        if (_policies.TryGetValue("artcraft", out var ac) && ac.Backing == ResourceBacking.LocalContainer && profile.IsEnabled("artcraft"))
        {
            os = os.WithHosting<ArtCraftModuleHosting, ArtCraftModuleOptions>(artcraft => artcraft.WithHostedApps());
        }

        if (_policies.TryGetValue(SandboxModuleHosting.ModuleName, out var sb) && sb.Backing == ResourceBacking.LocalContainer && profile.IsEnabled(SandboxModuleHosting.ModuleName))
        {
            os = os.WithHosting<SandboxModuleHosting>();
        }

        return os;
    }

    private static void EnsureNotLocalProcess(ResourceBacking backing, string resource)
    {
        if (backing == ResourceBacking.LocalProcess)
        {
            throw new ArgumentException(
                $"{resource} does not support {nameof(ResourceBacking)}.{nameof(ResourceBacking.LocalProcess)}; use {nameof(ResourceBacking.LocalContainer)}, {nameof(ResourceBacking.Cloud)}, or {nameof(ResourceBacking.Off)}.",
                nameof(backing));
        }
    }
}
