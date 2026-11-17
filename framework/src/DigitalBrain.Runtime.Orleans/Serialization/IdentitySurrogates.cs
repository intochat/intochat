using DigitalBrain.Capabilities.Identity;
using Orleans;

namespace DigitalBrain.Runtime.Orleans.Serialization;

/// <summary>Orleans wire form of <see cref="Account"/>.</summary>
[GenerateSerializer, Alias("identity.account")]
internal struct AccountSurrogate
{
    [Id(0)] public string AccountId;
    [Id(1)] public string Name;
    [Id(2)] public string OwnerPrincipalId;
    [Id(3)] public DateTimeOffset CreatedAt;
}

[RegisterConverter]
internal sealed class AccountSurrogateConverter : IConverter<Account, AccountSurrogate>
{
    public Account ConvertFromSurrogate(in AccountSurrogate surrogate) => new()
    {
        AccountId = surrogate.AccountId ?? "",
        Name = surrogate.Name ?? "",
        OwnerPrincipalId = surrogate.OwnerPrincipalId ?? "",
        CreatedAt = surrogate.CreatedAt,
    };

    public AccountSurrogate ConvertToSurrogate(in Account value) => new()
    {
        AccountId = value.AccountId,
        Name = value.Name,
        OwnerPrincipalId = value.OwnerPrincipalId,
        CreatedAt = value.CreatedAt,
    };
}

/// <summary>Orleans wire form of <see cref="Member"/>.</summary>
[GenerateSerializer, Alias("identity.member")]
internal struct MemberSurrogate
{
    [Id(0)] public string PrincipalId;
    [Id(1)] public string AccountId;
    [Id(2)] public string BrainId;
    [Id(3)] public MemberRole Role;
    [Id(4)] public string DisplayName;
    [Id(5)] public DateTimeOffset JoinedAt;
}

[RegisterConverter]
internal sealed class MemberSurrogateConverter : IConverter<Member, MemberSurrogate>
{
    public Member ConvertFromSurrogate(in MemberSurrogate surrogate) => new()
    {
        PrincipalId = surrogate.PrincipalId ?? "",
        AccountId = surrogate.AccountId ?? "",
        BrainId = surrogate.BrainId ?? "",
        Role = surrogate.Role,
        DisplayName = surrogate.DisplayName ?? "",
        JoinedAt = surrogate.JoinedAt,
    };

    public MemberSurrogate ConvertToSurrogate(in Member value) => new()
    {
        PrincipalId = value.PrincipalId,
        AccountId = value.AccountId,
        BrainId = value.BrainId,
        Role = value.Role,
        DisplayName = value.DisplayName,
        JoinedAt = value.JoinedAt,
    };
}

/// <summary>Orleans wire form of <see cref="DigitalBrain.Capabilities.Identity.BrainEntry"/>.</summary>
[GenerateSerializer, Alias("identity.brain-entry")]
internal struct BrainEntrySurrogate
{
    [Id(0)] public string Id;
    [Id(1)] public BrainKind Kind;
}

[RegisterConverter]
internal sealed class BrainEntrySurrogateConverter : IConverter<DigitalBrain.Capabilities.Identity.BrainEntry, BrainEntrySurrogate>
{
    public DigitalBrain.Capabilities.Identity.BrainEntry ConvertFromSurrogate(in BrainEntrySurrogate surrogate)
        => new(surrogate.Id ?? "", surrogate.Kind);

    public BrainEntrySurrogate ConvertToSurrogate(in DigitalBrain.Capabilities.Identity.BrainEntry value) => new()
    {
        Id = value.Id,
        Kind = value.Kind,
    };
}

/// <summary>Orleans wire form of <see cref="BrainProvision"/>.</summary>
[GenerateSerializer, Alias("identity.brain-provision")]
internal struct BrainProvisionSurrogate
{
    [Id(0)] public Member Member;
    [Id(1)] public bool NewBrain;
}

[RegisterConverter]
internal sealed class BrainProvisionSurrogateConverter : IConverter<BrainProvision, BrainProvisionSurrogate>
{
    public BrainProvision ConvertFromSurrogate(in BrainProvisionSurrogate surrogate)
        => new(surrogate.Member, surrogate.NewBrain);

    public BrainProvisionSurrogate ConvertToSurrogate(in BrainProvision value) => new()
    {
        Member = value.Member,
        NewBrain = value.NewBrain,
    };
}

/// <summary>Orleans wire form of <see cref="CallDecision"/>.</summary>
[GenerateSerializer, Alias("enforcement.call-decision")]
internal struct CallDecisionSurrogate
{
    [Id(0)] public bool Allowed;
    [Id(1)] public CallDenial? Denial;
    [Id(2)] public string? Explanation;
    [Id(3)] public bool ConsumesOnce;
}

[RegisterConverter]
internal sealed class CallDecisionSurrogateConverter : IConverter<CallDecision, CallDecisionSurrogate>
{
    public CallDecision ConvertFromSurrogate(in CallDecisionSurrogate surrogate) => new()
    {
        Allowed = surrogate.Allowed,
        Denial = surrogate.Denial,
        Explanation = surrogate.Explanation,
        ConsumesOnce = surrogate.ConsumesOnce,
    };

    public CallDecisionSurrogate ConvertToSurrogate(in CallDecision value) => new()
    {
        Allowed = value.Allowed,
        Denial = value.Denial,
        Explanation = value.Explanation,
        ConsumesOnce = value.ConsumesOnce,
    };
}

/// <summary>Orleans wire form of <see cref="Grant"/>.</summary>
[GenerateSerializer, Alias("identity.grant")]
internal struct GrantSurrogate
{
    [Id(0)] public string AppId;
    [Id(1)] public string SemanticTypeId;
    [Id(2)] public GrantMode Mode;
    [Id(3)] public string WorkspaceId;
    [Id(4)] public string? ConversationId;
    [Id(5)] public DateTimeOffset GrantedAt;
    [Id(6)] public bool Revoked;
    [Id(7)] public string? TargetNeuronKey;
    [Id(8)] public string Id;
    [Id(9)] public string Owner;
    [Id(10)] public string Grantee;
    [Id(11)] public string Target;
    [Id(12)] public string Scope;
    [Id(13)] public DateTimeOffset? Expires;
    [Id(14)] public bool Delegable;
}

[RegisterConverter]
internal sealed class GrantSurrogateConverter : IConverter<Grant, GrantSurrogate>
{
    public Grant ConvertFromSurrogate(in GrantSurrogate surrogate) => new()
    {
        AppId = surrogate.AppId ?? "",
        SemanticTypeId = surrogate.SemanticTypeId ?? "",
        Mode = surrogate.Mode,
        WorkspaceId = surrogate.WorkspaceId ?? "",
        ConversationId = surrogate.ConversationId,
        GrantedAt = surrogate.GrantedAt,
        Revoked = surrogate.Revoked,
        TargetNeuronKey = surrogate.TargetNeuronKey,
        Id = surrogate.Id ?? "",
        Owner = surrogate.Owner ?? "",
        Grantee = surrogate.Grantee ?? "",
        Target = surrogate.Target ?? "",
        Scope = surrogate.Scope ?? "",
        Expires = surrogate.Expires,
        Delegable = surrogate.Delegable,
    };

    public GrantSurrogate ConvertToSurrogate(in Grant value) => new()
    {
        AppId = value.AppId,
        SemanticTypeId = value.SemanticTypeId,
        Mode = value.Mode,
        WorkspaceId = value.WorkspaceId,
        ConversationId = value.ConversationId,
        GrantedAt = value.GrantedAt,
        Revoked = value.Revoked,
        TargetNeuronKey = value.TargetNeuronKey,
        Id = value.Id,
        Owner = value.Owner,
        Grantee = value.Grantee,
        Target = value.Target,
        Scope = value.Scope,
        Expires = value.Expires,
        Delegable = value.Delegable,
    };
}
