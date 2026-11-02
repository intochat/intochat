using System.Reflection;
using DigitalBrain;

namespace IntoChat.OS.Authority.Identity.Handles;

[DigitalBrain.Neuron(IHandleDirectory.TypeName)]
public sealed class HandleDirectoryNeuron : DigitalBrain.Neuron<HandleState>, IHandleDirectory
{
    private static readonly FieldInfo StateField =
        typeof(DigitalBrain.Neuron<HandleState>).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private async Task Save(HandleState next)
    {
        await Context.State.Save(next, []);
        StateField.SetValue(this, next);
    }

    public async Task<bool> Claim(string brainId)
    {
        if (string.IsNullOrEmpty(State.BrainId))
        {
            await Save(new HandleState { BrainId = brainId, ClaimedAt = DateTimeOffset.UtcNow });
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
