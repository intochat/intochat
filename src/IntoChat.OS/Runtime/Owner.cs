namespace IntoChat.OS;

/// <summary>The account a brain belongs to. Identity as a value, never a capability.</summary>
/// <remarks>
/// Whether a given caller may act for this owner is the platform's question, answered outside the
/// ring against the caller context. This value only says whose the brain is.
/// </remarks>
/// <param name="AccountId">The owning account.</param>
[GenerateSerializer, Alias("brain.owner")]
public sealed record Owner([property: Id(0)] string AccountId);
