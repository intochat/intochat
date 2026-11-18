namespace IntoChat.OS.Modules.Apps.CodeBlocks;

internal sealed record ClientBinding(string AppKey, string RunId, string[] Needs, ClientStep[] Steps);

internal sealed record ClientGrantsState(string[] Approved, ClientBinding[] Bindings)
{
    internal const int KeptSteps = 50;
    internal const int KeptApproved = 500;

    public ClientGrantsState() : this([], []) { }

    internal ClientBinding? Of(string appKey) => Bindings.FirstOrDefault(binding => binding.AppKey == appKey);

    internal ClientGrantsState With(ClientBinding binding) => this with { Bindings = [.. Bindings.Where(item => item.AppKey != binding.AppKey), binding] };
}

[DigitalBrain.Signal("client.grants.changed")]
internal sealed record ClientGrantsChanged(string Subject) : DigitalBrain.Signal;

// One per brain (key client/grants). Approving a need set is the person's act through the code block run's confirmation; this neuron
// only remembers it. A binding lives while the block is deployed; the edge asks Allowed and records every call it forwards or refuses.
[DigitalBrain.Neurons.Neuron("client.grants")]
internal sealed class ClientGrantsNeuron : DigitalBrain.Neurons.Neuron<ClientGrantsState>, IClientGrants
{
    public Task<bool> IsApproved(string needsHash) => Task.FromResult(State.Approved.Contains(needsHash, StringComparer.Ordinal));

    public async Task Approve(string needsHash, string[] needs)
    {
        if (State.Approved.Contains(needsHash, StringComparer.Ordinal)) { return; }
        await Save(State with { Approved = [.. State.Approved.TakeLast(ClientGrantsState.KeptApproved - 1), needsHash] }, new ClientGrantsChanged(needsHash));
    }

    public Task Bind(string appKey, string runId, string[] needs)
        => Save(State.With(new ClientBinding(appKey, runId, needs, [])), new ClientGrantsChanged(appKey));

    public async Task Release(string appKey)
    {
        if (State.Of(appKey) is null) { return; }
        await Save(State with { Bindings = [.. State.Bindings.Where(binding => binding.AppKey != appKey)] }, new ClientGrantsChanged(appKey));
    }

    public Task<string[]> Allowed(string appKey) => Task.FromResult(State.Of(appKey)?.Needs ?? []);

    public Task<string?> RunOf(string appKey) => Task.FromResult(State.Of(appKey)?.RunId);

    public async Task Record(string appKey, string call, bool allowed)
    {
        if (State.Of(appKey) is not { } binding) { return; }
        var step = new ClientStep(call, allowed, Context.Time.GetUtcNow());
        await Save(State.With(binding with { Steps = [.. binding.Steps.TakeLast(ClientGrantsState.KeptSteps - 1), step] }), new ClientGrantsChanged(appKey));
    }

    public Task<ClientStep[]> Steps(string appKey) => Task.FromResult(State.Of(appKey)?.Steps ?? []);
}
