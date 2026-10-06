using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Apps;

internal sealed class AppsRegistryResources(IDigitalBrain brain) : IRegistryResourceProvider
{
    public string Id => "apps";
    public RegistryCapability Summary => new(Id, "Installed apps and their operations. Browse provider 'apps' to choose an app.", []);
    public Task<RegistryDiscovery> Discover(CancellationToken ct) => Browse(0, 10, ct);

    public async Task<RegistryDiscovery> Browse(int offset, int limit, CancellationToken ct)
    {
        var names = await brain.Get<IAppIndex>(BrainScope.CurrentId()).List().WaitAsync(ct);
        var capabilities = new List<RegistryCapability>();
        var errors = new List<RegistryDiscoveryError>();
        foreach (var name in names.Skip(offset).Take(limit))
        {
            try { if (await Select("apps:" + name, ct) is { } capability) { capabilities.Add(capability); } }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { errors.Add(new(Id, "An installed app could not be described.")); }
        }
        return new([.. capabilities], [.. errors], names.Length > offset + limit ? offset + limit : null);
    }

    public async Task<RegistryCapability?> Select(string id, CancellationToken ct)
    {
        if (!id.StartsWith("apps:", StringComparison.Ordinal)) { return null; }
        var name = id[5..];
        var app = await brain.Get<IApp>(IApp.Key(BrainScope.CurrentId(), name)).Read().WaitAsync(ct);
        if (app.Status is not (AppStatus.Live or AppStatus.Building)) { return null; }
        return new(id, app.Manifest.Title + ": " + app.Manifest.Description,
            [.. app.Manifest.Operations.Select(operation => AppToolName.For(name, operation.Name))],
            JsonSerializer.Serialize(new { name, operations = app.Manifest.Operations }), "apps", name);
    }
}
