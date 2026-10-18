using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using IntoChat.OS.Framework;
using IntoChat.OS.Authority.Hosting;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Authority.Workshop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Metadata;
using Orleans.Serialization;

namespace IntoChat.OS.Authority.Hosted;

public static class HostedRecoveryHost
{
    public static bool IsRequested(IConfiguration configuration) => configuration.GetValue<bool>(HostedRuntimeOptions.SectionName + ":RecoveryOnly");

    public static void Configure(WebApplicationBuilder builder, IReadOnlyCollection<ServiceDescriptor> hostServices)
    {
        if (!IsRequested(builder.Configuration)) { throw new InvalidOperationException("Recovery mode must be explicit."); }
        // Keep only web hosting services captured before runtime composition. No silo, reminders, module startup or background jobs start.
        foreach (var descriptor in builder.Services.Where(x => x.ServiceType == typeof(IHostedService) && !hostServices.Contains(x)).ToArray())
        { builder.Services.Remove(descriptor); }
        builder.Services.AddSingleton<HostedExportStateTypes>();
    }

    public static void Map(WebApplication app)
    {
        var scope = app.Services.GetRequiredService<IOptions<HostedRuntimeOptions>>().Value;
        if (!scope.RecoveryOnly || !scope.IsConfigured) { throw new InvalidOperationException("Recovery host requires a complete authenticated scope."); }
        app.MapGet("/runtime/management/v1/observation", async (HttpContext http, BrainLogs logs, RuntimeMachineAuthentication authentication, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!await authentication.AuthenticateAsync(http, ct)) { return Results.Unauthorized(); }
            var storage = await logs.CheckStorageAsync(BrainScope.Create(scope.AccountId, scope.BrainId).Id, ct);
            var version = typeof(HostedRecoveryHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            return Results.Ok(new HostedRuntimeObservation(1, version, scope.AccountId, scope.BrainId, scope.RuntimeId, storage, storage, storage));
        }).AllowAnonymous();
        app.MapGet("/runtime/management/v1/export", async (HttpContext http, BrainLogs logs, HostedExportStateTypes types,
            IOptions<WorkshopOptions> workshop, RuntimeMachineAuthentication authentication, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!await authentication.AuthenticateAsync(http, ct)) { http.Response.StatusCode = 401; return; }
            var log = await logs.For(BrainScope.Create(scope.AccountId, scope.BrainId).Id).WaitAsync(ct);
            var data = log.Export(types.Decode);
            http.Response.ContentType = "application/zip";
            await using var archive = await ZipArchive.CreateAsync(http.Response.Body, ZipArchiveMode.Create, true, null, ct);
            await using (var state = archive.CreateEntry("brain.json").Open())
            { await JsonSerializer.SerializeAsync(state, data, cancellationToken: ct); }
            await using (var manifest = archive.CreateEntry("README.txt").Open())
            {
                var text = "Customer export: readable JSON neuron state and artifacts. Platform authority, account credentials, connector credentials, workspace sessions and reminder registrations are omitted. This is a portable data export, not a byte-for-byte runtime backup. The JSON includes source type and state identifiers. Unknown state types are counted as omissions; encrypted backup recovery preserves the full runtime.\n";
                await manifest.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), ct);
            }
            if (!string.IsNullOrWhiteSpace(workshop.Value.Root) && Directory.Exists(workshop.Value.Root))
            { await AddArtifactsAsync(archive, Path.GetFullPath(workshop.Value.Root), "", ct); }
        }).AllowAnonymous();
    }

    private static async Task AddArtifactsAsync(ZipArchive archive, string directory, string prefix, CancellationToken ct)
    {
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            // Reparse points must never escape the restored artifact tree.
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
            var name = prefix + item.Name;
            if (item is DirectoryInfo child) { await AddArtifactsAsync(archive, child.FullName, name + "/", ct); }
            else
            {
                await using var source = File.OpenRead(item.FullName);
                await using var destination = archive.CreateEntry("artifacts/" + name).Open();
                await source.CopyToAsync(destination, ct);
            }
        }
    }
}

internal sealed class HostedExportStateTypes
{
    private readonly Dictionary<(string Grain, string State), Type> types = [];
    private readonly ObjectSerializer serializer;
    private readonly string scope;

    public HostedExportStateTypes(IServiceProvider services, ObjectSerializer serializer, IOptions<HostedRuntimeOptions> options)
    {
        this.serializer = serializer;
        scope = BrainScope.Create(options.Value.AccountId, options.Value.BrainId).Id;
        var providers = services.GetServices<IGrainTypeProvider>().ToArray();
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic).SelectMany(Types))
        {
            if (type.IsAbstract || type.ContainsGenericParameters || type.Assembly == typeof(BrainLogs).Assembly) { continue; }
            foreach (var parameter in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SelectMany(x => x.GetParameters()))
            {
                var storage = parameter.GetCustomAttribute<PersistentStateAttribute>();
                if (storage?.StorageName != IntoChatOSNames.ClusterGrainStorage || !parameter.ParameterType.IsGenericType ||
                    parameter.ParameterType.GetGenericTypeDefinition() != typeof(IPersistentState<>)) { continue; }
                var state = parameter.ParameterType.GetGenericArguments()[0];
                if (!StateExportPolicy.Default.Allows(storage.StateName, state)) { continue; }
                foreach (var provider in providers)
                {
                    if (provider.TryGetGrainType(type, out var grain)) { types.TryAdd((grain.ToString(), storage.StateName), state); break; }
                }
            }
        }
    }

    public object? Decode(string grain, string state, byte[] bytes)
    {
        if (grain.StartsWith('$')) { return null; }
        var id = GrainId.Parse(grain);
        if (!BrainKey.BelongsTo(id.Key.ToString(), scope) || !types.TryGetValue((id.Type.ToString(), state), out var type)) { return null; }
        return serializer.Deserialize(bytes, type);
    }

    private static Type[] Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>().ToArray(); }
    }
}
