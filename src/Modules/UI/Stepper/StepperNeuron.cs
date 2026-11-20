using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Stepper.Signals;

namespace IntoChat.OS.Modules.UI.Stepper;

[Neuron("ui.stepper")]
internal sealed class StepperNeuron : UiNeuronBase<StepperState>, IStepper
{
    public async Task<StepperState> Configure(IReadOnlyList<StepperStep> steps, string? activeStepId = null, string title = "", bool allowInteractiveSelection = true)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var normalizedSteps = steps
            .Select(s => s with { Status = StepperStatuses.Normalize(s.Status) })
            .ToArray();
        var resolvedActive = activeStepId
            ?? normalizedSteps.FirstOrDefault(s => s.Status == StepperStatuses.Active)?.Id
            ?? normalizedSteps.FirstOrDefault()?.Id;

        var next = State with
        {
            Version = State.Version + 1,
            Title = title?.Trim() ?? "",
            Steps = normalizedSteps,
            ActiveStepId = resolvedActive,
            AllowInteractiveSelection = allowInteractiveSelection,
        };
        await Save(next, new StepperChanged(Id.FullKey(), next.Version, next.ActiveStepId));
        return next;
    }

    public async Task<StepperState> Select(string stepId)
    {
        if (!State.AllowInteractiveSelection)
        {
            throw new InvalidOperationException("Interactive step selection is disabled.");
        }
        if (State.Steps.All(s => !string.Equals(s.Id, stepId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Unknown step '{stepId}'.", nameof(stepId));
        }

        var next = State with { Version = State.Version + 1, ActiveStepId = stepId };
        await Save(next, new StepperChanged(Id.FullKey(), next.Version, next.ActiveStepId));
        return next;
    }

    public async Task<StepperState> SetStepStatus(string stepId, string status, string? subtitle = null)
    {
        var idx = Array.FindIndex(State.Steps, s => string.Equals(s.Id, stepId, StringComparison.Ordinal));
        if (idx < 0)
        {
            throw new ArgumentException($"Unknown step '{stepId}'.", nameof(stepId));
        }

        var normalized = StepperStatuses.Normalize(status);
        var updated = State.Steps.ToArray();
        updated[idx] = updated[idx] with
        {
            Status = normalized,
            Subtitle = subtitle ?? updated[idx].Subtitle,
        };

        var active = normalized == StepperStatuses.Active ? stepId : State.ActiveStepId;
        var next = State with
        {
            Version = State.Version + 1,
            Steps = updated,
            ActiveStepId = active,
        };
        await Save(next, new StepperChanged(Id.FullKey(), next.Version, next.ActiveStepId));
        return next;
    }

    public async Task<StepperState> Advance()
    {
        if (State.Steps.Length == 0)
        {
            return State;
        }

        var updated = State.Steps.ToArray();
        var currentIdx = Array.FindIndex(updated, s => string.Equals(s.Id, State.ActiveStepId, StringComparison.Ordinal));
        if (currentIdx < 0)
        {
            currentIdx = 0;
        }

        updated[currentIdx] = updated[currentIdx] with { Status = StepperStatuses.Done };
        string? nextActive = updated[currentIdx].Id;
        if (currentIdx + 1 < updated.Length)
        {
            var nextIdx = currentIdx + 1;
            nextActive = updated[nextIdx].Id;
            if (updated[nextIdx].Status == StepperStatuses.Pending)
            {
                updated[nextIdx] = updated[nextIdx] with { Status = StepperStatuses.Active };
            }
        }

        var next = State with
        {
            Version = State.Version + 1,
            Steps = updated,
            ActiveStepId = nextActive,
        };
        await Save(next, new StepperChanged(Id.FullKey(), next.Version, next.ActiveStepId));
        return next;
    }

    public Task<StepperState> Read() => Task.FromResult(State);
}
