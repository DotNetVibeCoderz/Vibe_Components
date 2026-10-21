namespace Blazor.AI.LocalModels;

/// <summary>Defaults for the Hugging Face components. Every model id can be overridden per component.</summary>
public sealed class LocalModelOptions
{
    /// <summary>Hugging Face access token (needed for gated models and the hosted Inference API).</summary>
    public string? HuggingFaceToken { get; set; }

    /// <summary>Where downloaded models are cached. Defaults to the Hugging Face cache (~/.cache/huggingface).</summary>
    public string? CacheRoot { get; set; }

    public string SentimentModel { get; set; } = "distilbert/distilbert-base-uncased-finetuned-sst-2-english";

    public string NerModel { get; set; } = "dslim/distilbert-NER";

    public string EmbeddingModel { get; set; } = "sentence-transformers/all-MiniLM-L6-v2";

    public string ImageModel { get; set; } = "WinKawaks/vit-tiny-patch16-224";

    public string ClipModel { get; set; } = "openai/clip-vit-base-patch32";

    public string OcrModel { get; set; } = "microsoft/trocr-small-printed";

    public string ChatModel { get; set; } = "HuggingFaceTB/SmolLM2-360M-Instruct";

    /// <summary>Hosted Inference API base (hf-inference provider).</summary>
    public string InferenceEndpoint { get; set; } = "https://router.huggingface.co/hf-inference/models/";

    /// <summary>Speech-to-text model for the hosted API.</summary>
    public string SpeechModel { get; set; } = "openai/whisper-large-v3-turbo";

    /// <summary>Text-to-speech model for the hosted API.</summary>
    public string TextToSpeechModel { get; set; } = "facebook/mms-tts-eng";

    /// <summary>Unload models that have not been used for this long.</summary>
    public TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Where a Hugging Face component runs its model.</summary>
public enum InferenceEngine
{
    /// <summary>Downloaded and run in-process on CPU with HF.Net (private, offline after download).</summary>
    Local,

    /// <summary>The hosted Hugging Face Inference API (needs a token with credits).</summary>
    HostedApi,
}
