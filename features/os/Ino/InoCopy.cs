// Ino's chat error strings (INT-20), in plain voice and never with exception text: the exception goes only to
// the server log, by type. Edit wording here; the status line, composer and presentation all read these.
public static class InoCopy
{
    public const string TurnFailed = "Ino couldn't answer. Try again.";
    // The transcript's copy is drawn by the Flutter chat renderer (app/ui/lib/src/chat/chat_copy.dart), pinned to
    // these by OsInoLinkFacts.TheChatRendererUsesInoCopy.
    public const string EmptyTranscript = "Ask Ino anything, or open an app from the dock.";
    public const string AppNotInstalled = "Not installed";
    public const string StepFailed = "This step didn't work.";
    public const string StartFailed = "Couldn't start the answer. Try again.";
    public const string NewChatFailed = "Couldn't start a new chat. Try again.";
    public const string OpenFailed = "Couldn't open Ino. Try again.";
    // The surface could not be published (for example IView refused it): said instead of a blank Ino.
    public const string ShowFailed = "Couldn't show Ino's window. Try reopening it.";
    public const string StopFailed = "Couldn't stop the answer. Try Stop again.";
    public const string DeliveryUnconfirmed = "Couldn't confirm your message was sent. Your draft is saved; Send retries the same message.";
    public const string EarlierAttemptFailed = "The earlier attempt didn't finish. Review your draft and send again.";
    public const string ChatUpdateFailed = "Couldn't update the chat. Try again.";
    public const string ModelChangeFailed = "Couldn't change the model. Try again.";
    public const string SetupFailed = "Couldn't set up the model. Try again.";
    public const string MakeFailed = "Couldn't make it. Try again.";
    public const string VoiceFailed = "Couldn't start voice input. Try again.";
    public const string RouterUnavailable = "Jev unavailable; asking the model.";
    public const string RouterFailed = "Jev couldn't carry it out; asking the model.";
    public const string BriefFailed = "Couldn't follow Memory's brief.";
    public const string ReviewCountFailed = "Couldn't check Memory's review count.";
    // Posted with a pasted workspace link's app card. It is the only text the model later reads for that entry.
    public const string LinkPosted = "Here's the app from your link.";
    public const string AnswerRunning = "A response is already running. Your draft is saved; send it when the response finishes.";
    public const string LinkFailed = "Couldn't show the app from your link. Try again.";
    public const string CaptureFailed = "Memory couldn't capture this conversation yet. It will try again.";

    // intended.cs clears its own scan notice after a healthy scan by this prefix (the older "...; retrying: <detail>"
    // notices share it).
    public const string ScanFailure = "Could not check pending messages; retrying.";
    public const string ScanFailurePrefix = "Could not check pending messages; retrying";

    public static readonly string[] Failures =
    [
        TurnFailed, StartFailed, NewChatFailed, OpenFailed, StopFailed, DeliveryUnconfirmed, EarlierAttemptFailed,
        ChatUpdateFailed, ModelChangeFailed, SetupFailed, MakeFailed, VoiceFailed, RouterUnavailable, RouterFailed,
        BriefFailed, ReviewCountFailed, CaptureFailed, ScanFailure, LinkFailed, ShowFailed,
    ];

    public static string RouteNotCarried(string chosen) => "Jev chose " + chosen + " but couldn't carry it out.";
}

public static partial class Ino
{
    // The person sees the copy; the server log gets the copy and the exception's type, never its message, which
    // can carry a value the person typed or a downstream service echoed.
    public static string Failure(string copy, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Console.Error.WriteLine("Ino: " + copy + " (" + error.GetType().Name + ")");
        return copy;
    }
}
