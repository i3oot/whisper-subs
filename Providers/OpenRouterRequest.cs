using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    internal static class OpenRouterRequest
    {
        internal static async Task<StringContent> CreateContentAsync(
            string audioPath,
            string model,
            string format,
            string language,
            CancellationToken cancellationToken)
        {
            var audio = await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false);
            return CreateContent(audio, model, format, language);
        }

        internal static StringContent CreateContent(
            byte[] audio,
            string model,
            string format,
            string language)
        {
            var request = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["input_audio"] = new Dictionary<string, string>
                {
                    ["data"] = Convert.ToBase64String(audio),
                    ["format"] = format,
                },
                ["response_format"] = "verbose_json",
                ["timestamp_granularities"] = new[] { "segment" },
            };

            if (!string.IsNullOrWhiteSpace(language)
                && !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                request["language"] = language;
            }

            return new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
        }
    }
}
