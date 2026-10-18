namespace IntoChat.OS.AI;

internal interface IConditionallyAvailable
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
