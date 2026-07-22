using System;
using System.Collections.Generic;

namespace WhisperSubs.Providers
{
    internal enum RemoteApiProtocol
    {
        OpenAi,
        OpenRouter
    }

    internal enum RemoteAudioFormat
    {
        Wav,
        Mp3
    }

    internal sealed record RemoteAudioOptions(
        RemoteApiProtocol Protocol,
        RemoteAudioFormat Format,
        int ChunkSeconds,
        int BitrateKbps)
    {
        internal const int DefaultOpenRouterChunkSeconds = 600;
        internal const int DefaultMp3BitrateKbps = 64;

        public bool IsOpenRouter => Protocol == RemoteApiProtocol.OpenRouter;
        public string Extension => Format == RemoteAudioFormat.Mp3 ? "mp3" : "wav";
        public string ContentType => Format == RemoteAudioFormat.Mp3 ? "audio/mpeg" : "audio/wav";

        public static RemoteAudioOptions Resolve(
            string? protocol,
            string apiUrl,
            string? audioFormat,
            int chunkSeconds,
            int bitrateKbps)
        {
            var normalizedProtocol = (protocol ?? string.Empty).Trim().ToLowerInvariant();
            var resolvedProtocol = normalizedProtocol switch
            {
                "openrouter" => RemoteApiProtocol.OpenRouter,
                "openai" => RemoteApiProtocol.OpenAi,
                _ => IsOpenRouterUrl(apiUrl) ? RemoteApiProtocol.OpenRouter : RemoteApiProtocol.OpenAi,
            };

            var normalizedFormat = (audioFormat ?? string.Empty).Trim().ToLowerInvariant();
            var resolvedFormat = normalizedFormat switch
            {
                "mp3" => RemoteAudioFormat.Mp3,
                "wav" => RemoteAudioFormat.Wav,
                _ => resolvedProtocol == RemoteApiProtocol.OpenRouter
                    ? RemoteAudioFormat.Mp3
                    : RemoteAudioFormat.Wav,
            };

            var resolvedChunkSeconds = chunkSeconds > 0
                ? chunkSeconds
                : resolvedProtocol == RemoteApiProtocol.OpenRouter
                    ? DefaultOpenRouterChunkSeconds
                    : 0;
            var resolvedBitrate = bitrateKbps is >= 16 and <= 320
                ? bitrateKbps
                : DefaultMp3BitrateKbps;

            return new RemoteAudioOptions(
                resolvedProtocol,
                resolvedFormat,
                resolvedChunkSeconds,
                resolvedBitrate);
        }

        internal static bool IsOpenRouterUrl(string apiUrl)
            => Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
                && (string.Equals(uri.Host, "openrouter.ai", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Plans overlapping source windows and non-overlapping keep windows. The overlap gives Whisper
        /// context at chunk boundaries; assigning a segment by its midpoint prevents duplicate subtitles.
        /// </summary>
        internal static IReadOnlyList<RemoteAudioChunkPlan> PlanChunks(
            double durationSeconds,
            int chunkSeconds,
            double overlapSeconds = 1.0)
        {
            if (durationSeconds <= 0)
                return Array.Empty<RemoteAudioChunkPlan>();

            if (chunkSeconds <= 0 || durationSeconds <= chunkSeconds)
            {
                return new[] { new RemoteAudioChunkPlan(0, durationSeconds, 0, durationSeconds) };
            }

            var result = new List<RemoteAudioChunkPlan>();
            for (double keepStart = 0; keepStart < durationSeconds; keepStart += chunkSeconds)
            {
                var keepEnd = Math.Min(durationSeconds, keepStart + chunkSeconds);
                var sourceStart = Math.Max(0, keepStart - overlapSeconds);
                var sourceEnd = Math.Min(durationSeconds, keepEnd + overlapSeconds);
                result.Add(new RemoteAudioChunkPlan(
                    sourceStart,
                    sourceEnd - sourceStart,
                    keepStart,
                    keepEnd));
            }

            return result;
        }
    }

    internal sealed record RemoteAudioChunkPlan(
        double SourceStartSeconds,
        double SourceDurationSeconds,
        double KeepStartSeconds,
        double KeepEndSeconds);
}
