using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI;

internal sealed class LegacyDefaultsAlias(IConfiguration? configuration = null, ILogger<LegacyDefaultsAlias>? logger = null)
    : IPostConfigureOptions<AIOptions>
{
    private static readonly (string Key, ModelRole Role)[] Mappings =
    [
        ("Model", ModelRole.Chat),
        ("Embedding", ModelRole.Embed),
        ("Transcription", ModelRole.Hear),
        ("SystemOne", ModelRole.Fast),
        ("Image", ModelRole.Image),
    ];

    public void PostConfigure(string? name, AIOptions options)
        => Apply(options, configuration, logger);

    internal static void Apply(AIOptions options, IConfiguration? configuration, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (configuration is null) { return; }

        var section = configuration.GetSection(AIKeys.LegacyDefault);
        if (!section.Exists()) { return; }

        foreach (var (key, role) in Mappings)
        {
            var raw = section[key];
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            if (options.Roles.Device.ContainsKey(role) || options.Roles.Cloud.ContainsKey(role)) { continue; }

            var card = KnownModels.Find(raw);
            if (card is null)
            {
                logger?.LogDebug("Ignoring unknown legacy default {Key} = '{Value}'.", key, raw);
                continue;
            }

            options.Roles.Device[role] = card.Id.Value;
            options.Roles.Cloud[role] = card.Id.Value;
        }

        if (string.IsNullOrWhiteSpace(options.Reasoning) && section["Reasoning"] is { Length: > 0 } reasoning)
        {
            options.Reasoning = reasoning.Trim();
        }

        if (options.MaxOutputTokens is null && int.TryParse(section["MaxOutputTokens"], System.Globalization.CultureInfo.InvariantCulture, out var maxTokens))
        {
            options.MaxOutputTokens = maxTokens;
        }
    }
}
