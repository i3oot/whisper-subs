using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    internal sealed record PreparedRemoteAudioChunk(
        string Path,
        string ContentType,
        string FileName,
        double SourceStartSeconds,
        double SourceDurationSeconds,
        double KeepStartSeconds,
        double KeepEndSeconds);

    internal sealed class PreparedRemoteAudio : IDisposable
    {
        private readonly string? _temporaryDirectory;

        public PreparedRemoteAudio(
            IReadOnlyList<PreparedRemoteAudioChunk> chunks,
            string? temporaryDirectory)
        {
            Chunks = chunks;
            _temporaryDirectory = temporaryDirectory;
        }

        public IReadOnlyList<PreparedRemoteAudioChunk> Chunks { get; }

        public void Dispose()
        {
            if (string.IsNullOrWhiteSpace(_temporaryDirectory))
                return;

            try
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; the OS temp directory remains the final backstop.
            }
        }
    }

    internal static class RemoteAudioPreparer
    {
        private const double PcmWavBytesPerSecond = 32000.0;

        public static async Task<PreparedRemoteAudio> PrepareAsync(
            string sourceWavPath,
            RemoteAudioOptions options,
            CancellationToken cancellationToken)
        {
            var durationSeconds = EstimatePcmWavDuration(sourceWavPath);
            var plans = RemoteAudioOptions.PlanChunks(
                durationSeconds,
                options.ChunkSeconds,
                options.IsOpenRouter ? 1.0 : 0.0);
            if (plans.Count == 0)
                throw new InvalidOperationException("The extracted audio is empty.");

            if (plans.Count == 1
                && options.Format == RemoteAudioFormat.Wav
                && plans[0].SourceStartSeconds == 0
                && Math.Abs(plans[0].SourceDurationSeconds - durationSeconds) < 0.01)
            {
                return new PreparedRemoteAudio(
                    new[]
                    {
                        new PreparedRemoteAudioChunk(
                            sourceWavPath,
                            options.ContentType,
                            "audio.wav",
                            0,
                            durationSeconds,
                            0,
                            durationSeconds)
                    },
                    temporaryDirectory: null);
            }

            var ffmpeg = FindFfmpegExecutable()
                ?? throw new InvalidOperationException(
                    "FFmpeg is required to compress or chunk audio for this remote worker, but it was not found.");
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"whispersubs_remote_{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);

            try
            {
                var chunks = new List<PreparedRemoteAudioChunk>(plans.Count);
                for (var index = 0; index < plans.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var plan = plans[index];
                    var fileName = $"audio_{index:D4}.{options.Extension}";
                    var outputPath = Path.Combine(temporaryDirectory, fileName);
                    await EncodeChunkAsync(
                        ffmpeg,
                        sourceWavPath,
                        outputPath,
                        plan,
                        options,
                        cancellationToken).ConfigureAwait(false);
                    chunks.Add(new PreparedRemoteAudioChunk(
                        outputPath,
                        options.ContentType,
                        fileName,
                        plan.SourceStartSeconds,
                        plan.SourceDurationSeconds,
                        plan.KeepStartSeconds,
                        plan.KeepEndSeconds));
                }

                return new PreparedRemoteAudio(chunks, temporaryDirectory);
            }
            catch
            {
                try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
                throw;
            }
        }

        internal static double EstimatePcmWavDuration(string path)
        {
            var bytes = new FileInfo(path).Length;
            return Math.Max(0, bytes - 44) / PcmWavBytesPerSecond;
        }

        private static async Task EncodeChunkAsync(
            string ffmpeg,
            string sourcePath,
            string outputPath,
            RemoteAudioChunkPlan plan,
            RemoteAudioOptions options,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(plan.SourceStartSeconds.ToString("F3", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(plan.SourceDurationSeconds.ToString("F3", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add("-vn");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("16000");
            startInfo.ArgumentList.Add("-c:a");
            if (options.Format == RemoteAudioFormat.Mp3)
            {
                startInfo.ArgumentList.Add("libmp3lame");
                startInfo.ArgumentList.Add("-b:a");
                startInfo.ArgumentList.Add($"{options.BitrateKbps}k");
            }
            else
            {
                startInfo.ArgumentList.Add("pcm_s16le");
            }
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add(outputPath);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException(
                    $"FFmpeg failed to prepare remote audio chunk (exit {process.ExitCode}): {stderr}");
            }
        }

        private static string? FindFfmpegExecutable()
        {
            var candidates = new[]
            {
                "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                "ffmpeg",
                "/usr/bin/ffmpeg",
            };

            foreach (var candidate in candidates)
            {
                if (Path.IsPathRooted(candidate))
                {
                    if (File.Exists(candidate)) return candidate;
                    continue;
                }

                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    if (process is null) continue;
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (process.WaitForExit(5000) && process.ExitCode == 0)
                        return candidate;
                    try { process.Kill(); } catch { }
                }
                catch
                {
                    // Try the next conventional location.
                }
            }

            return null;
        }
    }
}
