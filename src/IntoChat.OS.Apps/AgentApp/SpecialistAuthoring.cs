using IntoChat.OS.Modules.Apps;
using IntoChat.Treg;

namespace IntoChat.OS.Apps.AgentApp;

/// <summary>Input payload for creating a custom specialist bot from Unified Messenger or Apps.</summary>
public sealed record SpecialistDraft(
    string? Title,
    string? Handle,
    string? Instructions,
    string? Description = null,
    string[]? AllowedContracts = null,
    string[]? ToolSources = null,
    string? MemoryScope = null,
    string[]? Starters = null,
    Dictionary<string, string>? Settings = null,
    Dictionary<string, string>? Connections = null);

/// <summary>Outcome of validating and preparing a specialist bot creation or <c>SKILL.md</c> import.</summary>
public sealed record SpecialistAuthoringResult(
    bool Succeeded,
    string? AppId = null,
    string? Handle = null,
    string? Title = null,
    AppManifest? Manifest = null,
    string? Error = null)
{
    public static SpecialistAuthoringResult Fail(string error) => new(false, Error: error);
}

/// <summary>
/// Pure validation and manifest preparation for creating custom specialist bots and importing <c>SKILL.md</c> files.
/// Validates everything fail-closed before any neuron write so validation errors leave zero partial writes.
/// </summary>
public static class SpecialistAuthoring
{
    public static SpecialistAuthoringResult PrepareCreate(
        SpecialistDraft draft,
        IEnumerable<(string AppId, string? Handle)> existingSpecialists,
        string? brainId = null)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!AgentAppPolicy.TryNormalizeHandle(draft.Handle, out var normalizedHandle, out var handleError))
        {
            return SpecialistAuthoringResult.Fail(handleError ?? "Invalid specialist handle.");
        }

        if (string.IsNullOrWhiteSpace(draft.Instructions))
        {
            return SpecialistAuthoringResult.Fail("Specialist instructions are required.");
        }

        if (!TryParseMemoryScope(draft.MemoryScope, out var memoryScope, out var scopeError))
        {
            return SpecialistAuthoringResult.Fail(scopeError!);
        }

        var appId = normalizedHandle[1..]; // strip leading '@' -> safe lowercase ASCII slug
        var title = string.IsNullOrWhiteSpace(draft.Title) ? HumanizeSlug(appId) : draft.Title.Trim();
        var description = string.IsNullOrWhiteSpace(draft.Description) ? $"Custom specialist {normalizedHandle}" : draft.Description.Trim();

        try
        {
            AgentAppPolicy.EnsureUniqueHandle(normalizedHandle, appId, existingSpecialists, allowSelfUpdate: false);
            var allowedContracts = AgentAppPolicy.ValidateAllowedContracts(draft.AllowedContracts);
            var cleanStarters = (draft.Starters ?? [])
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Select(static s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var cleanToolSources = (draft.ToolSources ?? [])
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Select(static s => s.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var manifest = AppManifest.ForSpecialist(
                title: title,
                description: description,
                agent: new AppAgent(
                    Instructions: draft.Instructions.Trim(),
                    Starters: cleanStarters.Length > 0 ? cleanStarters : null,
                    Tools: null,
                    Handle: normalizedHandle,
                    AllowedContracts: allowedContracts.Length > 0 ? allowedContracts : null,
                    ToolSources: cleanToolSources.Length > 0 ? cleanToolSources : null,
                    MemoryScope: memoryScope));

            // Verify AgentAppPolicy projection (including same-brain ToolSources) succeeds before any write.
            _ = AgentAppPolicy.Definition(manifest, draft.Connections, appId, brainId);

            return new SpecialistAuthoringResult(
                Succeeded: true,
                AppId: appId,
                Handle: normalizedHandle,
                Title: manifest.Title,
                Manifest: manifest);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return SpecialistAuthoringResult.Fail(ex.Message);
        }
    }

    public static SpecialistAuthoringResult PrepareSkillImport(
        string? fileName,
        string? markdown,
        IEnumerable<(string AppId, string? Handle)> existingSpecialists,
        string? handleOverride = null,
        string[]? allowedContractsOverride = null,
        string[]? toolSourcesOverride = null,
        string? memoryScopeOverride = null,
        bool includeTregSlot = false,
        string? brainId = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return SpecialistAuthoringResult.Fail("SKILL.md content is required.");
        }

        MemoryScope? parsedScope = null;
        if (!string.IsNullOrWhiteSpace(memoryScopeOverride))
        {
            if (!TryParseMemoryScope(memoryScopeOverride, out var scope, out var scopeError))
            {
                return SpecialistAuthoringResult.Fail(scopeError!);
            }

            parsedScope = scope;
        }

        try
        {
            var effectiveName = string.IsNullOrWhiteSpace(fileName) ? "SKILL.md" : fileName.Trim();
            var file = new SkillFile(effectiveName, markdown);
            var manifest = SkillImport.ToApp(
                file,
                handle: handleOverride,
                allowedContracts: allowedContractsOverride,
                toolSources: toolSourcesOverride,
                memoryScope: parsedScope,
                includeTregSlot: includeTregSlot);

            var appId = !string.IsNullOrWhiteSpace(manifest.Agent?.Handle)
                ? manifest.Agent.Handle[1..]
                : SkillImport.AppIdOf(file);

            var resolvedHandle = !string.IsNullOrWhiteSpace(manifest.Agent?.Handle)
                ? manifest.Agent.Handle
                : AgentAppPolicy.NormalizeHandle(appId);

            AgentAppPolicy.EnsureUniqueHandle(resolvedHandle, appId, existingSpecialists, allowSelfUpdate: false);
            _ = AgentAppPolicy.ValidateAllowedContracts(manifest.Agent?.AllowedContracts);

            var finalManifest = string.Equals(manifest.Agent?.Handle, resolvedHandle, StringComparison.Ordinal)
                ? manifest
                : (manifest with { Agent = manifest.Agent! with { Handle = resolvedHandle } }).Validated();

            _ = AgentAppPolicy.Definition(finalManifest, null, appId, brainId);

            return new SpecialistAuthoringResult(
                Succeeded: true,
                AppId: appId,
                Handle: resolvedHandle,
                Title: finalManifest.Title,
                Manifest: finalManifest);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return SpecialistAuthoringResult.Fail(ex.Message);
        }
    }

    public static async Task<AppSnapshot> CreateAndConfigureAsync(
        string appId,
        AppManifest manifest,
        Func<string, IApp> getApp,
        Dictionary<string, string>? settings = null,
        Dictionary<string, string>? connections = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(getApp);

        var app = getApp(appId);
        var created = await app.Create(manifest, string.Empty);
        if ((settings is { Count: > 0 }) || (connections is { Count: > 0 }))
        {
            return await app.Configure(settings ?? [], connections ?? []);
        }

        return created;
    }

    public static bool TryParseMemoryScope(string? raw, out MemoryScope scope, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            scope = MemoryScope.None;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "none":
                scope = MemoryScope.None;
                return true;
            case "isolated":
            case "isolatedbotscope":
            case "isolated-bot-scope":
            case "isolated_bot_scope":
                scope = MemoryScope.IsolatedBotScope;
                return true;
            default:
                scope = MemoryScope.None;
                error = $"Unknown memory scope '{raw.Trim()}'. Choose 'none' or 'isolated'.";
                return false;
        }
    }

    private static string HumanizeSlug(string slug)
    {
        var parts = slug.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? "Specialist"
            : string.Join(' ', parts.Select(static p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
