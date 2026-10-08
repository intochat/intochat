namespace DigitalBrain.Apps;

public static class AppCalls
{
    public static async Task<string> Ask(this IApp app, string operation, string input = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = await app.Invoke(new(Guid.NewGuid(), operation, input));
        return await app.AwaitResponse(invocation.Id, cancellationToken);
    }

    public static async Task<string> AwaitResponse(this IApp app, Guid invocationId, CancellationToken cancellationToken = default)
    {
        var invocation = await app.Await(invocationId).WaitAsync(cancellationToken);
        if (invocation.Status == InvocationStatus.Failed) { throw new InvalidOperationException(invocation.Error ?? "The app failed."); }
        return invocation.Output ?? "";
    }
}
