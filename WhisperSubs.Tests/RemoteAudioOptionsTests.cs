using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class RemoteAudioOptionsTests
{
    [Fact]
    public void Resolve_OpenRouterUrl_UsesMp3AndTenMinuteChunks()
    {
        var options = RemoteAudioOptions.Resolve(
            "auto", "https://openrouter.ai/api", "auto", 0, 64);

        Assert.True(options.IsOpenRouter);
        Assert.Equal(RemoteAudioFormat.Mp3, options.Format);
        Assert.Equal(600, options.ChunkSeconds);
        Assert.Equal("audio/mpeg", options.ContentType);
    }

    [Fact]
    public void Resolve_ExistingWorker_PreservesUnchunkedWavDefaults()
    {
        var options = RemoteAudioOptions.Resolve(
            "auto", "http://gpu-box:8000", "auto", 0, 64);

        Assert.False(options.IsOpenRouter);
        Assert.Equal(RemoteAudioFormat.Wav, options.Format);
        Assert.Equal(0, options.ChunkSeconds);
    }

    [Fact]
    public void Resolve_ExplicitProtocolOverridesHostDetection()
    {
        var options = RemoteAudioOptions.Resolve(
            "openai", "https://openrouter.ai/api", "wav", 120, 96);

        Assert.False(options.IsOpenRouter);
        Assert.Equal(RemoteAudioFormat.Wav, options.Format);
        Assert.Equal(120, options.ChunkSeconds);
        Assert.Equal(96, options.BitrateKbps);
    }

    [Fact]
    public void PlanChunks_AddsOverlapButKeepsWindowsDisjoint()
    {
        var chunks = RemoteAudioOptions.PlanChunks(1250, 600);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new RemoteAudioChunkPlan(0, 601, 0, 600), chunks[0]);
        Assert.Equal(new RemoteAudioChunkPlan(599, 602, 600, 1200), chunks[1]);
        Assert.Equal(new RemoteAudioChunkPlan(1199, 51, 1200, 1250), chunks[2]);
    }

    [Fact]
    public void PlanChunks_CanDisableOverlapForSrtReturningProviders()
    {
        var chunks = RemoteAudioOptions.PlanChunks(650, 600, overlapSeconds: 0);

        Assert.Equal(new RemoteAudioChunkPlan(0, 600, 0, 600), chunks[0]);
        Assert.Equal(new RemoteAudioChunkPlan(600, 50, 600, 650), chunks[1]);
    }
}
