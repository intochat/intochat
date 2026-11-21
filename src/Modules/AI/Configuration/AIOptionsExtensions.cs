using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI;

public static class AIOptionsExtensions
{
    public static AIOptions WithModel(this AIOptions options, ModelCard card)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(card);
        var id = card.Id.Value;
        if (IsSystemOne(card))
        {
            if (!options.Hosting.SystemOnes.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                options.Hosting.SystemOnes.Add(id);
            }
        }
        else if (card.Can.HasFlag(Abilities.Text))
        {
            if (!options.Hosting.Llms.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                options.Hosting.Llms.Add(id);
            }
        }

        if (card.Can.HasFlag(Abilities.Embed) && !options.Hosting.Embeddings.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            options.Hosting.Embeddings.Add(id);
        }

        return options;
    }

    public static AIOptions WithRole(this AIOptions options, Role role, ModelCard card, Where map = Where.Cloud)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(card);
        options.WithModel(card);
        TargetMap(options, map)[role] = card.Id.Value;
        return options;
    }

    public static AIOptions WithoutRole(this AIOptions options, Role role, Where map = Where.Cloud)
    {
        ArgumentNullException.ThrowIfNull(options);
        TargetMap(options, map).Remove(role);
        return options;
    }

    public static AIOptions WithModelEndpoint(this AIOptions options, AiProvider provider, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
        { throw new ArgumentException("A model endpoint must be an absolute HTTP(S) URI.", nameof(endpoint)); }
        options.Provider(provider).Endpoint = endpoint.OriginalString;
        return options;
    }

    public static AIOptions WithoutLocalModels(this AIOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Hosting.Llms.RemoveAll(static id => KnownModels.Find(id)?.Where == Where.Device);
        options.Hosting.Embeddings.RemoveAll(static id => KnownModels.Find(id)?.Where == Where.Device);
        options.Hosting.SystemOnes.RemoveAll(static id => KnownModels.Find(id)?.Where == Where.Device);
        RemoveDeviceRoles(options.Roles.Device);
        RemoveDeviceRoles(options.Roles.Cloud);
        return options;
    }

    internal static bool IsSystemOne(ModelCard card)
        => card.Provider == AiProvider.TypeSafe || card.Id == KnownModels.LocalJev.Id;

    private static Dictionary<Role, string> TargetMap(AIOptions options, Where map)
        => map == Where.Device ? options.Roles.Device : options.Roles.Cloud;

    private static void RemoveDeviceRoles(Dictionary<Role, string> roles)
    {
        foreach (var role in roles.Keys.ToArray())
        {
            if (KnownModels.Find(roles[role])?.Where == Where.Device)
            {
                roles.Remove(role);
            }
        }
    }
}
