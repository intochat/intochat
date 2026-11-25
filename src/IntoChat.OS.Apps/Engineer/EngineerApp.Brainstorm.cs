using System.Globalization;
using DigitalBrain;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Modules.UI.Chat;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Engineer;

// The brainstorm thread (control.chatThread) owns its messages, so Engineer writes them: the current run's transcript after every save
// that changes it, and once, the earlier runs' transcripts the old ui.chat neuron kept.
public sealed partial class EngineerApp
{
    // Where the ui.chat neuron Engineer drew before kept the brainstorm messages of every earlier run. Read once and imported into the
    // brainstorm child (owner decision 11); OWED.md names the reader and the flag for deletion.
    public const string LegacyBrainstormChatKey = "engineer/chat/brainstorm";

    [Slot(Key = LegacyBrainstormChatKey, Required = false)] private partial IChatThread? LegacyBrainstormChat { get; }

    // The current run's transcript as thread messages; ids are "{run}-turn-{i}", the ids the old ui.chat sync used, so a run's turns
    // replace themselves in place and earlier runs stay above them.
    internal static ThreadMessage[] TranscriptOf(EngineerWorkspaceState state)
        => [.. state.BrainstormTurns.Select((turn, i) => new ThreadMessage(
            string.Create(CultureInfo.InvariantCulture, $"{state.ActiveRunId}-turn-{i}"),
            turn.Role == "user" ? "user" : "assistant",
            $"**[{turn.Speaker} · {turn.Perspective}]** {turn.Message}"))];

    // Content the thread owns cannot be a prop (paper 40, rule 3): it is sent when it differs from what was last sent. After a
    // reactivation nothing was sent yet, and the import is idempotent by id.
    private async Task PushTranscriptAsync()
    {
        var transcript = TranscriptOf(State);
        if (_pushedTranscript is { } pushed && pushed.SequenceEqual(transcript))
        {
            return;
        }

        try
        {
            await SendToBrainstorm(new ImportMessages(transcript));
            _pushedTranscript = transcript;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _pushedTranscript = null;
            _logger.LogDebug(error, "EngineerApp could not write the brainstorm transcript to its thread.");
        }
    }

    // Once per Engineer: the old ui.chat neuron's messages go into the brainstorm child, then the flag is saved. A failed read leaves the
    // flag unset, so the next activation tries again; a repeated import changes nothing (same ids).
    private async Task ImportLegacyBrainstormAsync()
    {
        if (State.BrainstormImported || TryBrainOrNull() is null)
        {
            return;
        }

        try
        {
            if (LegacyBrainstormChat is not { } old)
            {
                return;
            }

            var legacy = (await old.Read()).Messages;
            if (legacy.Length > 0)
            {
                await SendToBrainstorm(new ImportMessages([.. legacy.Select(FromLegacy)]));
            }

            await Show(State with { BrainstormImported = true });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "EngineerApp could not import the earlier brainstorm messages; it tries again on its next activation.");
        }
    }

    private static ThreadMessage FromLegacy(ChatMessage message)
        => new(message.Id, message.Role is "user" or "system" ? message.Role : "assistant", message.Text ?? string.Empty);

    private Task SendToBrainstorm(ImportMessages command)
        => Send(new Command(BrainstormKey, "control.importMessages",
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(command, typeof(ImportMessages))));
}
