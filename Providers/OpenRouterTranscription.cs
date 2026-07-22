using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WhisperSubs.Providers
{
    internal sealed record OpenRouterSegment(double Start, double End, string Text);

    internal sealed record OpenRouterTranscription(
        string Text,
        string Language,
        IReadOnlyList<OpenRouterSegment> Segments)
    {
        public static OpenRouterTranscription Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var text = root.TryGetProperty("text", out var textProperty)
                ? textProperty.GetString() ?? string.Empty
                : string.Empty;
            var language = root.TryGetProperty("language", out var languageProperty)
                ? languageProperty.GetString() ?? "auto"
                : "auto";

            var segments = new List<OpenRouterSegment>();
            if (root.TryGetProperty("segments", out var segmentArray)
                && segmentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segmentArray.EnumerateArray())
                {
                    if (!TryGetNumber(segment, "start", out var start)
                        || !TryGetNumber(segment, "end", out var end)
                        || end <= start)
                    {
                        continue;
                    }

                    var segmentText = segment.TryGetProperty("text", out var segmentTextProperty)
                        ? segmentTextProperty.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(segmentText))
                    {
                        segments.Add(new OpenRouterSegment(start, end, segmentText.Trim()));
                    }
                }
            }

            return new OpenRouterTranscription(text, language, segments);
        }

        public static string AppendSrt(
            StringBuilder destination,
            IEnumerable<OpenRouterSegment> segments,
            double sourceStartSeconds,
            double keepStartSeconds,
            double keepEndSeconds,
            ref int nextIndex)
        {
            foreach (var segment in segments)
            {
                var absoluteStart = sourceStartSeconds + segment.Start;
                var absoluteEnd = sourceStartSeconds + segment.End;
                var midpoint = absoluteStart + ((absoluteEnd - absoluteStart) / 2.0);
                if (midpoint < keepStartSeconds || midpoint >= keepEndSeconds)
                    continue;

                if (destination.Length > 0)
                    destination.AppendLine();
                destination.AppendLine(nextIndex.ToString(CultureInfo.InvariantCulture));
                destination.Append(FormatTimestamp(Math.Max(0, absoluteStart)))
                    .Append(" --> ")
                    .AppendLine(FormatTimestamp(Math.Max(absoluteStart, absoluteEnd)));
                destination.AppendLine(segment.Text.Trim());
                nextIndex++;
            }

            return destination.ToString();
        }

        private static bool TryGetNumber(JsonElement element, string propertyName, out double value)
        {
            value = 0;
            return element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out value);
        }

        private static string FormatTimestamp(double seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            var totalHours = (int)Math.Floor(time.TotalHours);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");
        }
    }
}
