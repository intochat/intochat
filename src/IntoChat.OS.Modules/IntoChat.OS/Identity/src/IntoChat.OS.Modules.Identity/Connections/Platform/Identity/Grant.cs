using System.Runtime.Serialization;

namespace IntoChat.OS.Connections.Identity;

public enum GrantMode
{
    Once = 0,
    ThisChat = 1,
    Always = 2,
}

public static class GrantRelations
{
    public const string View = "view";
    public const string Run = "run";
    public const string Post = "post";
    public const string Subscribe = "subscribe";
    public const string Install = "install";
    public const string Admin = "admin";
    public const string UseViaPrefix = "use-via:";

    public static bool IsUseVia(string? scope) => scope != null && scope.StartsWith(UseViaPrefix, StringComparison.Ordinal);
}

// Semantic type ids double as permission scopes, e.g. "person.birthDate".
[GenerateSerializer, Alias("identity.grant")]
public sealed record Grant
{
    public Grant() { }

    public Grant(string id, string owner, string grantee, string target, string scope, DateTimeOffset? expires = null, bool revoked = false)
    {
        Id = id;
        Owner = owner;
        Grantee = grantee;
        Target = target;
        Scope = scope;
        Expires = expires;
        Revoked = revoked;
        WorkspaceId = owner.StartsWith("brain:", StringComparison.Ordinal) ? owner["brain:".Length..] : owner;
        AppId = grantee.StartsWith("app:", StringComparison.Ordinal) ? grantee["app:".Length..] : grantee;
        SemanticTypeId = scope;
        TargetNeuronKey = target;
    }

    [Id(0)] public string AppId { get; init; } = "";
    [Id(1)] public string SemanticTypeId { get; init; } = "";
    [Id(2)] public GrantMode Mode { get; init; } = GrantMode.Always;
    [Id(3)] public string WorkspaceId { get; init; } = "";
    [Id(4)] public string? ConversationId { get; init; }
    [Id(5)] public DateTimeOffset GrantedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(6)] public bool Revoked { get; init; }
    [Id(7)] public string? TargetNeuronKey { get; init; }

    [Id(8)] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [Id(9)] public string Owner { get; init; } = "";
    [Id(10)] public string Grantee { get; init; } = "";
    [Id(11)] public string Target { get; init; } = "";
    [Id(12)] public string Scope { get; init; } = "";
    [Id(13)] public DateTimeOffset? Expires { get; init; }
    [Id(14)] public bool Delegable { get; init; }

    [IgnoreDataMember]
    public bool IsCanonical => Grantee.Length > 0 && Scope.Length > 0;

    public static Grant Tuple(
        string owner,
        string grantee,
        string target,
        string relation,
        DateTimeOffset? expires = null,
        bool delegable = false)
    {
        return new Grant(
            id: Guid.NewGuid().ToString("N"),
            owner: owner,
            grantee: grantee,
            target: target,
            scope: relation,
            expires: expires)
        {
            Delegable = !GrantRelations.IsUseVia(relation) && delegable,
        };
    }

    public static Grant Normalize(Grant g)
    {
        if (g.Grantee.Length > 0 && g.Target.Length > 0 && g.Scope.Length > 0)
        {
            return g;
        }

        return g with
        {
            Owner = g.WorkspaceId.StartsWith("brain:", StringComparison.Ordinal) ? g.WorkspaceId : "brain:" + g.WorkspaceId,
            Grantee = g.AppId.StartsWith("app:", StringComparison.Ordinal) ? g.AppId : "app:" + g.AppId,
            Target = g.TargetNeuronKey ?? "",
            Scope = g.SemanticTypeId,
        };
    }
}
