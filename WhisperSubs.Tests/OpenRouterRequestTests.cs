using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class OpenRouterRequestTests
{
    [Fact]
    public async Task CreateContent_UsesDocumentedBase64JsonShape()
    {
        using var content = OpenRouterRequest.CreateContent(
            new byte[] { 1, 2, 3 },
            "openai/whisper-large-v3",
            "mp3",
            "tr");

        Assert.Equal("application/json", content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("openai/whisper-large-v3", root.GetProperty("model").GetString());
        Assert.Equal("AQID", root.GetProperty("input_audio").GetProperty("data").GetString());
        Assert.Equal("mp3", root.GetProperty("input_audio").GetProperty("format").GetString());
        Assert.Equal("tr", root.GetProperty("language").GetString());
        Assert.Equal("verbose_json", root.GetProperty("response_format").GetString());
        Assert.Equal("segment", root.GetProperty("timestamp_granularities")[0].GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public async Task CreateContent_OmitsAutomaticLanguage(string language)
    {
        using var content = OpenRouterRequest.CreateContent(
            new byte[] { 1 },
            "model",
            "wav",
            language);

        using var document = JsonDocument.Parse(await content.ReadAsStringAsync());
        Assert.False(document.RootElement.TryGetProperty("language", out _));
    }
}
