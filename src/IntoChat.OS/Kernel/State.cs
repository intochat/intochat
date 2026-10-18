namespace IntoChat.OS;

/// <summary>What a brain persists: whose it is and when it came to be.</summary>
/// <param name="Owner">The account the brain belongs to.</param>
/// <param name="CreatedAt">When <see cref="IBrain.Create"/> succeeded.</param>
[GenerateSerializer, Alias("brain.state")]
[ExportableState]
public sealed record State([property: Id(0)] Owner Owner, [property: Id(1)] DateTimeOffset CreatedAt);
