using System.Text;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class OpenRouterTranscriptionTests
{
    [Fact]
    public void ParseAndAppendSrt_OffsetsAndFormatsSegments()
    {
        const string json = """
            {
              "text": "Hello world",
              "language": "en",
              "segments": [
                { "start": 0.25, "end": 1.5, "text": " Hello " },
                { "start": 1.5, "end": 2.75, "text": "world" }
              ]
            }
            """;
        var transcription = OpenRouterTranscription.Parse(json);
        var srt = new StringBuilder();
        var nextIndex = 3;

        OpenRouterTranscription.AppendSrt(
            srt, transcription.Segments, 600, 600, 1200, ref nextIndex);

        Assert.Equal("en", transcription.Language);
        Assert.Equal(5, nextIndex);
        Assert.Contains("3\n00:10:00,250 --> 00:10:01,500\nHello", Normalize(srt.ToString()));
        Assert.Contains("4\n00:10:01,500 --> 00:10:02,750\nworld", Normalize(srt.ToString()));
    }

    [Fact]
    public void AppendSrt_UsesMidpointToRemoveOverlapDuplicates()
    {
        var segments = new[]
        {
            new OpenRouterSegment(0.1, 0.5, "previous chunk"),
            new OpenRouterSegment(1.2, 2.0, "this chunk"),
        };
        var srt = new StringBuilder();
        var nextIndex = 1;

        OpenRouterTranscription.AppendSrt(
            srt, segments, sourceStartSeconds: 599,
            keepStartSeconds: 600, keepEndSeconds: 1200, ref nextIndex);

        Assert.DoesNotContain("previous chunk", srt.ToString());
        Assert.Contains("this chunk", srt.ToString());
        Assert.Equal(2, nextIndex);
    }

    [Fact]
    public void Parse_IgnoresMalformedAndEmptySegments()
    {
        const string json = """
            { "text": "x", "segments": [
              { "start": 1, "end": 1, "text": "zero" },
              { "start": 1, "end": 2, "text": " " },
              { "text": "missing times" }
            ] }
            """;

        Assert.Empty(OpenRouterTranscription.Parse(json).Segments);
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n");
}
