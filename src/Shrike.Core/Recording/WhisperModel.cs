namespace Shrike.Core.Recording;

/// <summary>
/// One downloadable whisper.cpp transcription model. Models are large and language-specific, so — unlike the
/// engine binary — they are an <b>opt-in, in-app download</b>, never shipped in the installer. English-only
/// models (<c>*.en</c>) are smaller and more accurate for English; the multilingual models cover everything
/// else. <see cref="Sha256"/> may be blank until pinned; the store verifies only when it is set.
/// </summary>
public sealed record WhisperModel(
    string Id,
    string DisplayName,
    string Language,
    long ApproxBytes,
    string FileName,
    string Url,
    string Sha256 = "")
{
    /// <summary>A friendly size like "142 MB" for the picker.</summary>
    public string ApproxSize => ApproxBytes >= 1024L * 1024 * 1024
        ? $"{ApproxBytes / (1024.0 * 1024 * 1024):0.0} GB"
        : $"{ApproxBytes / (1024.0 * 1024):0} MB";
}

/// <summary>
/// The catalog of transcription models Shrike offers to download, hosted as GGML files on Hugging Face.
/// Kept deliberately small — English (tiny/base/small) plus two multilingual options — so the picker is
/// approachable; more can be added without code changes elsewhere. UI-free.
/// </summary>
public static class WhisperModelCatalog
{
    // Canonical GGML weights published by the whisper.cpp project (ggerganov's HF repo hosts the models;
    // ggml-org/whisper.cpp is the source repo and 401s on model paths).
    private const string Base = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    // NOTE(release): pin each Sha256 by downloading once and recording the hash (leave blank = no verify).
    public static IReadOnlyList<WhisperModel> Models { get; } =
    [
        new("tiny.en",  "Tiny (English)",   "English",       75L * 1024 * 1024,  "ggml-tiny.en.bin",  Base + "ggml-tiny.en.bin"),
        new("base.en",  "Base (English)",   "English",      142L * 1024 * 1024,  "ggml-base.en.bin",  Base + "ggml-base.en.bin"),
        new("small.en", "Small (English)",  "English",      466L * 1024 * 1024,  "ggml-small.en.bin", Base + "ggml-small.en.bin"),
        new("base",     "Base (multilingual)",  "Multilingual", 142L * 1024 * 1024, "ggml-base.bin",  Base + "ggml-base.bin"),
        new("small",    "Small (multilingual)", "Multilingual", 466L * 1024 * 1024, "ggml-small.bin", Base + "ggml-small.bin"),
    ];

    /// <summary>The suggested default for a fresh English user — best accuracy/size balance.</summary>
    public const string DefaultId = "base.en";

    public static WhisperModel? Find(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => m.Id == id);

    public static WhisperModel Default => Find(DefaultId) ?? Models[0];
}
