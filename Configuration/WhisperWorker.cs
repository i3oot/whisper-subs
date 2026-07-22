namespace WhisperSubs.Configuration
{
    /// <summary>
    /// One configured extra transcription worker (v4.0 worker pool). This list is EMPTY for a normal
    /// single-server install — the plugin then just uses the host's own local whisper, exactly as today.
    /// A power user adds entries here to pool additional OpenAI-compatible endpoints (a second box, a NAS,
    /// a cloud API). Plain mutable class so it round-trips through the plugin's XML config.
    /// </summary>
    public class WhisperWorker
    {
        /// <summary>Stable id (dispatch/dedup key); survives a rename.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display label, e.g. "nas-igpu".</summary>
        public string Name { get; set; } = "";

        public bool Enabled { get; set; } = true;

        /// <summary>OpenAI-compatible base URL, e.g. <c>http://192.168.1.10:8080</c>.</summary>
        public string ApiUrl { get; set; } = "";

        /// <summary>Optional bearer token for this worker.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Model to request; empty = the worker's own default / any.</summary>
        public string Model { get; set; } = "";

        /// <summary>Simultaneous jobs this worker runs; keep 1 per single GPU. Default 1.</summary>
        public int MaxConcurrency { get; set; } = 1;

        /// <summary>Selection cost: 0 = free/local-priced (preferred); &gt;0 = paid, used only to burst. Default 0.</summary>
        public double CostWeight { get; set; } = 0;

        /// <summary>Whether this worker can translate to English. Default true.</summary>
        public bool CanTranslate { get; set; } = true;

        /// <summary>
        /// Remote API dialect: <c>auto</c>, <c>openai</c>, or <c>openrouter</c>.
        /// Auto detects OpenRouter from its hostname and otherwise preserves the existing OpenAI-compatible
        /// multipart/SRT behaviour.
        /// </summary>
        public string Protocol { get; set; } = "auto";

        /// <summary>
        /// Upload encoding: <c>auto</c>, <c>wav</c>, or <c>mp3</c>. Auto uses MP3 for OpenRouter and WAV for
        /// existing OpenAI-compatible/self-hosted workers.
        /// </summary>
        public string AudioFormat { get; set; } = "auto";

        /// <summary>
        /// Maximum seconds per remote request. Zero selects the protocol default: 600 seconds for OpenRouter
        /// and no chunking for existing OpenAI-compatible workers.
        /// </summary>
        public int ChunkSeconds { get; set; } = 0;

        /// <summary>MP3 bitrate in kbit/s when MP3 upload is selected. Default 64.</summary>
        public int AudioBitrateKbps { get; set; } = 64;
    }
}
