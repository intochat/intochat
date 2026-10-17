namespace DigitalBrain;

// A refusal word: this machine journaled changes the part never accepted while another machine wrote it; the
// journal is set aside, not replayed over the other machine's history.
[GenerateSerializer, Alias("framework.journal-conflict")]
public sealed class JournalConflictException(string message) : Exception(message);
