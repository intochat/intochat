namespace IntoChat.OS.Modules.Apps.Tests.Unit.Policies;

public sealed class FakePolicyModel(params string[] replies) : IPolicyModel
{
    private readonly Queue<string> _replies = new(replies);
    private readonly List<string> _prompts = [];
    private readonly List<string> _systems = [];

    public int Calls => _prompts.Count;

    public IReadOnlyList<string> Prompts => _prompts;

    public IReadOnlyList<string> Systems => _systems;

    public void Enqueue(params string[] next)
    {
        ArgumentNullException.ThrowIfNull(next);
        foreach (var reply in next)
        {
            _replies.Enqueue(reply);
        }
    }

    public Task<string> Complete(string brainId, string system, string prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _systems.Add(system);
        _prompts.Add(prompt);
        if (!_replies.TryDequeue(out var reply))
        {
            throw new InvalidOperationException("FakePolicyModel has no queued reply.");
        }

        return Task.FromResult(reply);
    }
}
