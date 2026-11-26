using System.Collections.Frozen;
using System.Globalization;

namespace IntoChat.OS.Modules.Devices.Runtimes.SherpaOnnx;

internal sealed record SherpaModelAssets(
    Uri KokoroArchiveUrl,
    long KokoroArchiveBytes,
    string KokoroArchiveSha256,
    Uri VadUrl,
    long VadBytes,
    string VadSha256)
{
    public long TotalBytes => KokoroArchiveBytes + VadBytes;

    public static SherpaModelAssets Default { get; } = new(
        SherpaModels.KokoroArchiveUrl,
        SherpaModels.KokoroArchiveBytes,
        SherpaModels.KokoroArchiveSha256,
        SherpaModels.VadUrl,
        SherpaModels.VadBytes,
        SherpaModels.VadSha256);
}

internal static class SherpaModels
{
    public const string SdkVersion = "2.0.1";
    public const string KokoroDirectoryName = "kokoro-multi-lang-v1_0";
    public const string KokoroArchiveName = "kokoro-multi-lang-v1_0.tar.bz2";
    public const string KokoroModelFileName = "model.onnx";
    public const string VadFileName = "silero_vad_v5.onnx";

    public static readonly Uri KokoroArchiveUrl =
        new("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2");

    public const long KokoroArchiveBytes = 349_886_476L;
    public const string KokoroArchiveSha256 = "60703707bc05ccd48d60e1eb723f38b07d6e474795a0b6a588d7220400e0f7a4";

    public static readonly Uri VadUrl =
        new("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad_v5.onnx");

    public const long VadBytes = 2_313_101L;
    public const string VadSha256 = "6b99cbfd39246b6706f98ec13c7c50c6b299181f2474fa05cbc8046acc274396";

    public const long TotalDownloadBytes = KokoroArchiveBytes + VadBytes;

    public const int KokoroSampleRate = 24_000;
    public const int DefaultSpeakerId = 0; // af_alloy (US English female)
    public const string DefaultVoiceName = "af_alloy";

    // Embedded in kokoro-multi-lang-v1_0/model.onnx (id2speaker metadata, 54 voices: 0..53).
    public static readonly FrozenDictionary<string, int> Speakers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["af_alloy"] = 0,
        ["af_aoede"] = 1,
        ["af_bella"] = 2,
        ["af_heart"] = 3,
        ["af_jessica"] = 4,
        ["af_kore"] = 5,
        ["af_nicole"] = 6,
        ["af_nova"] = 7,
        ["af_river"] = 8,
        ["af_sarah"] = 9,
        ["af_sky"] = 10,
        ["am_adam"] = 11,
        ["am_echo"] = 12,
        ["am_eric"] = 13,
        ["am_fenrir"] = 14,
        ["am_liam"] = 15,
        ["am_michael"] = 16,
        ["am_onyx"] = 17,
        ["am_puck"] = 18,
        ["am_santa"] = 19,
        ["bf_alice"] = 20,
        ["bf_emma"] = 21,
        ["bf_isabella"] = 22,
        ["bf_lily"] = 23,
        ["bm_daniel"] = 24,
        ["bm_fable"] = 25,
        ["bm_george"] = 26,
        ["bm_lewis"] = 27,
        ["ef_dora"] = 28,
        ["em_alex"] = 29,
        ["ff_siwis"] = 30,
        ["hf_alpha"] = 31,
        ["hf_beta"] = 32,
        ["hm_omega"] = 33,
        ["hm_psi"] = 34,
        ["if_sara"] = 35,
        ["im_nicola"] = 36,
        ["jf_alpha"] = 37,
        ["jf_gongitsune"] = 38,
        ["jf_nezumi"] = 39,
        ["jf_tebukuro"] = 40,
        ["jm_kumo"] = 41,
        ["pf_dora"] = 42,
        ["pm_alex"] = 43,
        ["pm_santa"] = 44,
        ["zf_xiaobei"] = 45,
        ["zf_xiaoni"] = 46,
        ["zf_xiaoxiao"] = 47,
        ["zf_xiaoyi"] = 48,
        ["zm_yunjian"] = 49,
        ["zm_yunxi"] = 50,
        ["zm_yunxia"] = 51,
        ["zm_yunyang"] = 52,
        ["em_santa"] = 53,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static int ResolveSpeakerId(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return DefaultSpeakerId;
        }

        var trimmed = voiceId.Trim();
        if (Speakers.TryGetValue(trimmed, out var id))
        {
            return id;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            && numeric >= 0
            && numeric < Speakers.Count)
        {
            return numeric;
        }

        return DefaultSpeakerId;
    }
}
