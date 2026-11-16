using DigitalBrain.Neurons;

namespace IntoChat.OS.Authority.Identity.Handles;

[Neuron(IHandleDirectory.TypeName)]
public sealed class HandleDirectoryNeuron : Neuron<HandleState>, IHandleDirectory
{
    private Task Save(HandleState next) => Store(next);

    public async Task<bool> Claim(string brainId)
    {
        if (string.IsNullOrEmpty(State.BrainId))
        {
            await Save(new HandleState { BrainId = brainId, ClaimedAt = Context.Time.GetUtcNow() });
            return true;
        }

        if (string.Equals(State.BrainId, brainId, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public async Task Release(string brainId)
    {
        if (string.Equals(State.BrainId, brainId, StringComparison.Ordinal))
        {
            await Save(new HandleState { BrainId = "" });
        }
    }

    public Task<string?> Resolve()
    {
        return Task.FromResult<string?>(string.IsNullOrEmpty(State.BrainId) ? null : State.BrainId);
    }
}
