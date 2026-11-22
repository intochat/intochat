using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Tests.Support.Fakes.AI;

/// <summary>
/// Test extensions for talking to a person's Ino assistant in journey tests.
/// </summary>
public static class PersonInoExtensions
{
    /// <summary>Returns a handle to <paramref name="person"/>'s default Ino conversation.</summary>
    public static InoConversation Ino(this Person person, string conversationId = "main")
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return new InoConversation(person, conversationId);
    }
}

/// <summary>
/// Thin wrapper over a person's default <see cref="IConversation"/> with Ino seated.
/// </summary>
public sealed class InoConversation
{
    private readonly Person _person;
    private readonly IConversation _conversation;

    internal InoConversation(Person person, string conversationId)
    {
        _person = person;
        var id = new NeuronId(person.Brain, IConversation.Type, IConversation.KeyOf(ConversationId.Parse(conversationId)));
        _conversation = person.Get<IConversation>(id);
    }

    /// <summary>The assistant's most recent reply text from <see cref="Say"/>.</summary>
    public string? LastReply { get; private set; }

    /// <summary>Sends <paramref name="text"/> to Ino, seating Ino if needed, and awaits turn completion.</summary>
    public async Task<string?> Say(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var before = await _conversation.Read().ConfigureAwait(false);
        if (!before.Participants.OfType<Participant.Assistant>().Any())
        {
            var definition = InoAgentDefinition.For(new UserSettingsState { DisplayName = _person.Name });
            await _conversation.Seat("ino", definition).ConfigureAwait(false);
        }

        var expectedRuns = before.Runs.Length + 1;
        await _conversation.Say(text).ConfigureAwait(false);

        var view = await WaitForRunAsync(_conversation, expectedRuns, ct).ConfigureAwait(false);
        var lastRun = view.Runs.LastOrDefault();
        LastReply = lastRun?.Reply
            ?? view.Entries.OfType<Said>().LastOrDefault(static e => e.By is Participant.Assistant)?.Text;
        return LastReply;
    }

    private static async Task<ConversationView> WaitForRunAsync(IConversation conversation, int expectedRuns, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var view = await conversation.Read().ConfigureAwait(false);
            if (view.Active is null && view.Runs.Length >= expectedRuns)
            {
                return view;
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        return await conversation.Read().ConfigureAwait(false);
    }
}
