using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhisperSubs.Controller;

namespace WhisperSubs.Providers
{
    public class RemoteWhisperProvider : ISubtitleProvider
    {
        private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            // Per-call deadlines (TranscriptionTimeout, applied via a linked CancellationTokenSource) are
            // the authority — so a long-but-healthy transcription is never guillotined and a hung call is
            // still bounded. The old fixed 30-minute timeout was the root of the multi-day stuck-task bug.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        private readonly ILogger _logger;
        private readonly string _apiUrl;
        private readonly string _model;
        private readonly string _apiKey;
        private readonly double _realtimeFactor;
        private readonly int _minTimeoutSeconds;
        private readonly int _maxTimeoutHours;
        private readonly RemoteAudioOptions _audioOptions;

        public string Name => "RemoteWhisper";

        /// <summary>Remote API returns its own timestamps; no local VAD involvement.</summary>
        public bool UsesVad => false;

        [ExcludeFromCodeCoverage(Justification = "Construction + HTTPS-key warning; no unit-testable logic")]
        public RemoteWhisperProvider(ILogger logger, string apiUrl, string model, string apiKey = "",
            double realtimeFactor = 6.0, int minTimeoutSeconds = 60, int maxTimeoutHours = 12,
            string protocol = "auto", string audioFormat = "auto", int chunkSeconds = 0,
            int audioBitrateKbps = 64)
        {
            _logger = logger;
            _apiUrl = apiUrl.TrimEnd('/');
            _model = model;
            _apiKey = (apiKey ?? string.Empty).Trim();
            _realtimeFactor = realtimeFactor;
            _minTimeoutSeconds = minTimeoutSeconds;
            _maxTimeoutHours = maxTimeoutHours;
            _audioOptions = RemoteAudioOptions.Resolve(
                protocol, _apiUrl, audioFormat, chunkSeconds, audioBitrateKbps);

            if (!string.IsNullOrEmpty(_apiKey) &&
                Uri.TryCreate(_apiUrl, UriKind.Absolute, out var uri) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "RemoteWhisper API key is configured with a non-HTTPS URL ({Scheme}). The key will be sent in cleartext. Consider switching to https://.",
                    uri.Scheme);
            }
        }

        private void ApplyAuthorization(HttpRequestMessage request)
        {
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
        }

        [ExcludeFromCodeCoverage(Justification = "HTTP I/O; the pure deadline policy is tested in TranscriptionTimeoutTests")]
        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false)
        {
            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            if (_audioOptions.IsOpenRouter)
            {
                if (translate)
                {
                    throw new NotSupportedException(
                        "OpenRouter does not expose the /v1/audio/translations endpoint required by WhisperSubs. " +
                        "Disable translation capability for this worker.");
                }

                return await TranscribeOpenRouterAsync(audioPath, language, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_audioOptions.Format != RemoteAudioFormat.Wav || _audioOptions.ChunkSeconds > 0)
            {
                return await TranscribePreparedOpenAiAsync(
                    audioPath, language, cancellationToken, translate).ConfigureAwait(false);
            }

            var endpoint = translate
                ? $"{_apiUrl}/v1/audio/translations"
                : $"{_apiUrl}/v1/audio/transcriptions";

            _logger.LogInformation("Sending audio to remote Whisper API: {Endpoint} [lang={Language}, translate={Translate}]",
                endpoint, language, translate);

            long audioBytes = new FileInfo(audioPath).Length;

            using var content = new MultipartFormDataContent();
            // Stream the WAV rather than File.ReadAllBytes: a 2h film is ~260 MB, and N parallel workers
            // buffering that in RAM is a direct OOM path. StreamContent's file stream is disposed with the
            // MultipartFormDataContent.
            var fileContent = new StreamContent(File.OpenRead(audioPath));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", "audio.wav");

            content.Add(new StringContent(_model), "model");
            content.Add(new StringContent("srt"), "response_format");

            if (!string.IsNullOrWhiteSpace(language) &&
                !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(new StringContent(language), "language");
            }

            var srt = await PostAudioAsync(endpoint, content, audioBytes, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(srt))
            {
                throw new InvalidOperationException("Remote Whisper API returned empty response");
            }

            _logger.LogInformation("Remote transcription complete, received {Length} characters of SRT", srt.Length);
            return srt;
        }

        private async Task<string> TranscribePreparedOpenAiAsync(
            string audioPath,
            string language,
            CancellationToken cancellationToken,
            bool translate)
        {
            var endpoint = translate
                ? $"{_apiUrl}/v1/audio/translations"
                : $"{_apiUrl}/v1/audio/transcriptions";
            using var prepared = await RemoteAudioPreparer.PrepareAsync(
                audioPath, _audioOptions, cancellationToken).ConfigureAwait(false);

            var combined = new StringBuilder();
            var nextIndex = 1;
            foreach (var chunk in prepared.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var content = CreateMultipartContent(chunk.Path, chunk.ContentType, chunk.FileName);
                content.Add(new StringContent(_model), "model");
                content.Add(new StringContent("srt"), "response_format");
                AddLanguage(content, language);

                var equivalentPcmBytes = (long)Math.Ceiling(
                    chunk.SourceDurationSeconds * TranscriptionTimeout.BytesPerAudioSecond);
                var chunkSrt = await PostAudioAsync(
                    endpoint, content, equivalentPcmBytes, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(chunkSrt))
                    continue;

                if (combined.Length > 0)
                    combined.AppendLine().AppendLine();
                combined.Append(WhisperProvider.OffsetSrt(
                    chunkSrt, chunk.SourceStartSeconds, nextIndex));
                nextIndex += WhisperProvider.CountSrtEntries(chunkSrt);
            }

            if (combined.Length == 0)
                throw new InvalidOperationException("Remote Whisper API returned empty response");

            return combined.ToString().TrimEnd();
        }

        private async Task<string> TranscribeOpenRouterAsync(
            string audioPath,
            string language,
            CancellationToken cancellationToken)
        {
            var endpoint = $"{_apiUrl}/v1/audio/transcriptions";
            using var prepared = await RemoteAudioPreparer.PrepareAsync(
                audioPath, _audioOptions, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Sending audio to OpenRouter in {ChunkCount} {Format} chunk(s) [lang={Language}]",
                prepared.Chunks.Count,
                _audioOptions.Extension,
                language);

            var srt = new StringBuilder();
            var nextIndex = 1;
            foreach (var chunk in prepared.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uploadBytes = new FileInfo(chunk.Path).Length;
                if (uploadBytes > 25_000_000)
                {
                    throw new InvalidOperationException(
                        $"Prepared OpenRouter chunk is {uploadBytes / 1_000_000.0:F1} MB, above the 25 MB multipart limit. " +
                        "Reduce the worker chunk duration or MP3 bitrate.");
                }

                using var content = CreateMultipartContent(chunk.Path, chunk.ContentType, chunk.FileName);
                content.Add(new StringContent(_model), "model");
                content.Add(new StringContent("verbose_json"), "response_format");
                content.Add(new StringContent("segment"), "timestamp_granularities[]");
                AddLanguage(content, language);

                // The timeout policy is based on uncompressed audio duration, not compressed upload bytes.
                var equivalentPcmBytes = (long)Math.Ceiling(
                    chunk.SourceDurationSeconds * TranscriptionTimeout.BytesPerAudioSecond);
                var json = await PostAudioAsync(
                    endpoint, content, equivalentPcmBytes, cancellationToken).ConfigureAwait(false);
                var transcription = OpenRouterTranscription.Parse(json);
                OpenRouterTranscription.AppendSrt(
                    srt,
                    transcription.Segments,
                    chunk.SourceStartSeconds,
                    chunk.KeepStartSeconds,
                    chunk.KeepEndSeconds,
                    ref nextIndex);
            }

            if (srt.Length == 0)
            {
                throw new InvalidOperationException(
                    "OpenRouter returned no timestamped segments. Select an OpenAI-compatible transcription " +
                    "model/provider that supports response_format=verbose_json.");
            }

            _logger.LogInformation(
                "OpenRouter transcription complete, received {EntryCount} subtitle entries",
                nextIndex - 1);
            return srt.ToString().TrimEnd();
        }

        [ExcludeFromCodeCoverage(Justification = "HTTP I/O; the pure deadline policy is tested in TranscriptionTimeoutTests")]
        public async Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            _logger.LogInformation("Detecting language via remote Whisper API for {AudioPath}", audioPath);

            long audioBytes = new FileInfo(audioPath).Length;

            using var content = CreateMultipartContent(audioPath, "audio/wav", "audio.wav");

            content.Add(new StringContent(_model), "model");
            content.Add(new StringContent("verbose_json"), "response_format");

            var endpoint = $"{_apiUrl}/v1/audio/transcriptions";
            var json = await PostAudioAsync(endpoint, content, audioBytes, cancellationToken).ConfigureAwait(false);

            var language = _audioOptions.IsOpenRouter
                ? OpenRouterTranscription.Parse(json).Language
                : ParseLanguage(json);

            language = NormalizeLangName(language);

            _logger.LogInformation("Remote language detection: {Language}", language);

            return (language, 0.0f);
        }

        private static MultipartFormDataContent CreateMultipartContent(
            string audioPath,
            string contentType,
            string fileName)
        {
            var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(File.OpenRead(audioPath));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(fileContent, "file", fileName);
            return content;
        }

        private static void AddLanguage(MultipartFormDataContent content, string language)
        {
            if (!string.IsNullOrWhiteSpace(language)
                && !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(new StringContent(language), "language");
            }
        }

        private static string ParseLanguage(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.TryGetProperty("language", out var langProp)
                ? langProp.GetString() ?? "auto"
                : "auto";
        }

        /// <summary>
        /// POSTs a prepared multipart body under a per-call deadline derived from the audio length
        /// (<see cref="TranscriptionTimeout"/>). A caller cancellation propagates as
        /// <see cref="OperationCanceledException"/>; the deadline elapsing surfaces as a
        /// <see cref="TimeoutException"/> so a stalled/unreachable endpoint fails fast and clearly instead
        /// of hanging (the multi-day stuck-task class of bug).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP I/O; the pure deadline policy is tested in TranscriptionTimeoutTests")]
        private async Task<string> PostAudioAsync(string endpoint, MultipartFormDataContent content, long audioBytes, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            ApplyAuthorization(request);

            var deadline = TranscriptionTimeout.Compute(audioBytes, _realtimeFactor, _minTimeoutSeconds, _maxTimeoutHours);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(deadline);

            try
            {
                using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                    throw new HttpRequestException($"Remote Whisper API returned {(int)response.StatusCode}: {errorBody}");
                }

                return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Remote Whisper API call exceeded its {deadline.TotalSeconds:F0}s deadline (endpoint slow or unreachable): {endpoint}");
            }
        }

        private static string NormalizeLangName(string lang)
        {
            if (lang.Length <= 3) return lang;

            return lang.ToLowerInvariant() switch
            {
                "english" => "en",
                "spanish" => "es",
                "french" => "fr",
                "german" => "de",
                "italian" => "it",
                "portuguese" => "pt",
                "russian" => "ru",
                "japanese" => "ja",
                "chinese" => "zh",
                "korean" => "ko",
                "dutch" => "nl",
                "polish" => "pl",
                "turkish" => "tr",
                "arabic" => "ar",
                "hindi" => "hi",
                "czech" => "cs",
                "greek" => "el",
                "hungarian" => "hu",
                "romanian" => "ro",
                "swedish" => "sv",
                "danish" => "da",
                "finnish" => "fi",
                "norwegian" => "no",
                "catalan" => "ca",
                "ukrainian" => "uk",
                "vietnamese" => "vi",
                "thai" => "th",
                "indonesian" => "id",
                "malay" => "ms",
                "hebrew" => "he",
                _ => lang,
            };
        }
    }
}
