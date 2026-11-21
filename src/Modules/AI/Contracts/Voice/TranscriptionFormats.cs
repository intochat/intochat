namespace IntoChat.OS.Modules.AI.Voice;

// Not decoration: the gpt-4o transcribe models accept only text and json, while
// verbose json and the subtitle formats are whisper-1's alone.
[Flags]
public enum TranscriptionFormats
{
    None = 0,
    Text = 1,
    Json = 1 << 1,
    VerboseJson = 1 << 2,
    Srt = 1 << 3,
    Vtt = 1 << 4,
}
