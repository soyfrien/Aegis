using Aegis.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aegis.Tests;

public class ReencodeDecisionTests
{
    private static MediaInfo Info(string? v = "h264", string? a = "aac", string container = "mov,mp4,m4a,3gp,3g2,mj2", string ext = ".mp4", string? pix = "yuv420p", string? tag = "avc1") =>
        new(container, ext, v, pix, tag, a, 60);

    [Theory]
    [InlineData(null, ReencodeMode.Off)]
    [InlineData("", ReencodeMode.Off)]
    [InlineData("off", ReencodeMode.Off)]
    [InlineData("AUTO", ReencodeMode.Auto)]
    [InlineData(" always ", ReencodeMode.Always)]
    public void Mode_parses(string? text, ReencodeMode expected)
    {
        Assert.True(ReencodeModes.TryParse(text, out var m));
        Assert.Equal(expected, m);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("x264")]
    [InlineData("1")]
    public void Unknown_mode_is_rejected(string text) => Assert.False(ReencodeModes.TryParse(text, out _));

    [Fact]
    public void Off_never_touches_the_file() =>
        Assert.Equal(ReencodeAction.None, FfmpegReencoder.Decide(ReencodeMode.Off, Info("vp9", "opus", "matroska,webm", ".webm")));

    [Fact]
    public void Always_transcodes_even_compatible_files() =>
        Assert.Equal(ReencodeAction.Transcode, FfmpegReencoder.Decide(ReencodeMode.Always, Info()));

    [Fact]
    public void Always_skips_files_without_video() =>
        Assert.Equal(ReencodeAction.None, FfmpegReencoder.Decide(ReencodeMode.Always, Info(v: null)));

    [Fact]
    public void Auto_skips_h264_aac_mp4() =>
        Assert.Equal(ReencodeAction.None, FfmpegReencoder.Decide(ReencodeMode.Auto, Info()));

    [Fact]
    public void Auto_skips_hevc_aac_mov_with_hvc1() =>
        Assert.Equal(ReencodeAction.None, FfmpegReencoder.Decide(ReencodeMode.Auto, Info("hevc", "aac", ext: ".mov", pix: "yuv420p10le", tag: "hvc1")));

    [Fact]
    public void Auto_skips_h264_without_audio() =>
        Assert.Equal(ReencodeAction.None, FfmpegReencoder.Decide(ReencodeMode.Auto, Info(a: null)));

    [Theory]
    [InlineData("vp9", "opus", "matroska,webm", ".webm")]
    [InlineData("av1", "aac", "mov,mp4,m4a,3gp,3g2,mj2", ".mp4")]
    [InlineData("vp8", "vorbis", "matroska,webm", ".webm")]
    [InlineData("mpeg4", "mp3", "avi", ".avi")]
    public void Auto_transcodes_unsupported_video(string v, string a, string container, string ext) =>
        Assert.Equal(ReencodeAction.Transcode, FfmpegReencoder.Decide(ReencodeMode.Auto, Info(v, a, container, ext)));

    [Fact]
    public void Auto_transcodes_10_bit_h264() =>
        Assert.Equal(ReencodeAction.Transcode, FfmpegReencoder.Decide(ReencodeMode.Auto, Info(pix: "yuv420p10le")));

    [Fact]
    public void Auto_remuxes_h264_aac_in_mkv() =>
        Assert.Equal(ReencodeAction.Remux, FfmpegReencoder.Decide(ReencodeMode.Auto, Info(container: "matroska,webm", ext: ".mkv")));

    [Fact]
    public void Auto_remuxes_hevc_with_hev1_tag_and_asks_for_hvc1() =>
        Assert.Equal(ReencodeAction.Remux, FfmpegReencoder.Decide(ReencodeMode.Auto, Info("hevc", "aac", tag: "hev1")));

    [Fact]
    public void Auto_only_converts_audio_when_video_is_fine() =>
        Assert.Equal(ReencodeAction.ConvertAudio, FfmpegReencoder.Decide(ReencodeMode.Auto, Info(a: "opus")));

    [Fact]
    public void Auto_keeps_original_when_probe_failed() =>
        Assert.Equal(ReencodeAction.None, FfmpegReencoder.Decide(ReencodeMode.Auto, null));
}

public class ReencodeProbeAndCommandTests
{
    private const string ProbeJson = """
        {"streams":[
          {"codec_name":"mjpeg","codec_type":"video","pix_fmt":"yuvj420p","codec_tag_string":"[0][0][0][0]"},
          {"codec_name":"vp9","codec_type":"video","pix_fmt":"yuv420p","codec_tag_string":"[0][0][0][0]"},
          {"codec_name":"opus","codec_type":"audio"},
          {"codec_name":"aac","codec_type":"audio"}],
         "format":{"format_name":"matroska,webm","duration":"125.500000"}}
        """;

    [Fact]
    public void Probe_json_is_parsed_and_cover_art_ignored()
    {
        var info = FfmpegReencoder.ParseProbe(ProbeJson, "/w/Clip.WEBM")!;
        Assert.Equal("vp9", info.VideoCodec);
        Assert.Equal("opus", info.AudioCodec);
        Assert.Equal("matroska,webm", info.Container);
        Assert.Equal(".webm", info.Extension);
        Assert.Equal(125.5, info.DurationSeconds);
    }

    [Fact]
    public void Garbage_probe_output_gives_null() => Assert.Null(FfmpegReencoder.ParseProbe("not json"));

    [Fact]
    public void Transcode_arguments_use_libx264_veryfast_crf23_yuv420p_faststart_aac_and_cap_1080p()
    {
        var a = FfmpegReencoder.BuildArguments(ReencodeAction.Transcode, "/w/in.webm", "/w/out.tmp");
        string After(string flag) => a[a.IndexOf(flag) + 1];
        Assert.Equal("libx264", After("-c:v"));
        Assert.Equal("veryfast", After("-preset"));
        Assert.Equal("23", After("-crf"));
        Assert.Equal("yuv420p", After("-pix_fmt"));
        Assert.Equal("aac", After("-c:a"));
        Assert.Equal("+faststart", After("-movflags"));
        Assert.Equal("2", After("-threads"));
        Assert.Equal("mp4", After("-f"));
        Assert.Contains("1080", After("-vf"));
        Assert.Contains("1920", After("-vf"));
        Assert.Equal("pipe:1", After("-progress"));
        Assert.Equal("/w/in.webm", After("-i"));
        Assert.Equal("/w/out.tmp", a[^1]);
        Assert.Contains("-nostdin", a);
        Assert.DoesNotContain("-tag:v", a);
    }

    [Fact]
    public void Remux_copies_streams_and_can_force_hvc1()
    {
        var a = FfmpegReencoder.BuildArguments(ReencodeAction.Remux, "in.mkv", "out.tmp", hvc1Tag: true);
        Assert.Equal("copy", a[a.IndexOf("-c") + 1]);
        Assert.DoesNotContain("libx264", a);
        Assert.Equal("hvc1", a[a.IndexOf("-tag:v") + 1]);
        Assert.Equal("+faststart", a[a.IndexOf("-movflags") + 1]);
    }

    [Fact]
    public void Audio_conversion_copies_video_and_encodes_aac()
    {
        var a = FfmpegReencoder.BuildArguments(ReencodeAction.ConvertAudio, "in.mp4", "out.tmp");
        Assert.Equal("copy", a[a.IndexOf("-c:v") + 1]);
        Assert.Equal("aac", a[a.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("libx264", a);
    }

    [Fact]
    public void Nothing_to_build_for_none() =>
        Assert.Throws<ArgumentException>(() => FfmpegReencoder.BuildArguments(ReencodeAction.None, "a", "b"));

    [Fact]
    public void Nice_wraps_the_command_on_linux_only()
    {
        var (file, args) = FfmpegReencoder.WithNice("ffmpeg", ["-i", "x"], 10, isLinux: true);
        Assert.Equal("nice", file);
        Assert.Equal(["-n", "10", "ffmpeg", "-i", "x"], args);
        Assert.Equal("ffmpeg", FfmpegReencoder.WithNice("ffmpeg", ["-i", "x"], 10, isLinux: false).File);
        Assert.Equal("ffmpeg", FfmpegReencoder.WithNice("ffmpeg", ["-i", "x"], 0, isLinux: true).File);
    }

    [Theory]
    [InlineData("out_time_us=30000000", 120.0, 25.0)]
    [InlineData("out_time_ms=60000000", 120.0, 50.0)]
    [InlineData("out_time_us=999999999", 120.0, 99.0)]
    public void Progress_lines_become_percentages(string line, double duration, double expected)
    {
        Assert.True(FfmpegReencoder.TryParseProgress(line, duration, out var pct));
        Assert.Equal(expected, pct, 1);
    }

    [Theory]
    [InlineData("progress=continue")]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time_us=-5")]
    [InlineData("")]
    public void Other_progress_lines_are_ignored(string line) => Assert.False(FfmpegReencoder.TryParseProgress(line, 100, out _));

    [Fact]
    public void Progress_needs_a_known_duration() => Assert.False(FfmpegReencoder.TryParseProgress("out_time_us=1000", null, out _));

    [Fact]
    public void Tool_paths_resolve_from_options()
    {
        Assert.Equal("ffprobe", FfmpegReencoder.ResolveTool(new AegisOptions(), "ffprobe"));
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Equal(Path.Combine(dir, "ffprobe"), FfmpegReencoder.ResolveTool(new AegisOptions { FfmpegPath = dir }, "ffprobe"));
            Assert.Equal(Path.Combine(dir, "ffprobe"), FfmpegReencoder.ResolveTool(new AegisOptions { FfmpegPath = Path.Combine(dir, "ffmpeg") }, "ffprobe"));
        }
        finally { Directory.Delete(dir); }
    }
}

public class ReencodeJobTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "aegis-reenc-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_work)) Directory.Delete(_work, true); }

    private sealed class Dl : IDownloader
    {
        public Task<string> DownloadAsync(string url, string directory, Action<double, string?> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(directory);
            var f = Path.Combine(directory, "clip.webm");
            File.WriteAllText(f, "original");
            return Task.FromResult(f);
        }
    }

    private sealed class Enc(Func<string, ReencodeMode, Action<double, string?>, CancellationToken, Task<ReencodeResult>> run) : IReencoder
    {
        public int Calls;
        public Task<ReencodeResult> ProcessAsync(string inputPath, ReencodeMode mode, Action<double, string?> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return run(inputPath, mode, progress, ct);
        }
    }

    private JobService Create(IReencoder enc, int timeoutMinutes = 30) => new(new Dl(), enc,
        Options.Create(new AegisOptions { WorkDir = _work, Reencode = new ReencodeOptions { TimeoutMinutes = timeoutMinutes } }), NullLogger<JobService>.Instance);

    private static async Task<Job> Finished(Job job)
    {
        for (var i = 0; i < 400 && job.State is JobState.Queued or JobState.Running; i++) await Task.Delay(25);
        return job;
    }

    [Fact]
    public async Task Off_does_not_call_the_encoder()
    {
        var enc = new Enc((_, _, _, _) => throw new InvalidOperationException("should not run"));
        var job = (await Finished(Create(enc).Start("https://example.com/v", ReencodeMode.Off)!));
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal("clip.webm", job.FileName);
        Assert.Equal("none", job.ReencodeResult);
        Assert.Equal(0, enc.Calls);
    }

    [Fact]
    public async Task Always_replaces_the_file_with_the_encoded_one_and_counts_it()
    {
        var enc = new Enc((input, mode, progress, _) =>
        {
            Assert.Equal(ReencodeMode.Always, mode);
            progress(50, "Re-encoding");
            var outp = Path.ChangeExtension(input, ".mp4");
            File.Delete(input);
            File.WriteAllText(outp, "encoded!");
            return Task.FromResult(new ReencodeResult(outp, ReencodeOutcome.Reencoded, "Re-encoded to H.264/AAC MP4 for iPhone."));
        });
        var svc = Create(enc);
        var job = await Finished(svc.Start("https://example.com/v", ReencodeMode.Always)!);
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal("clip.mp4", job.FileName);
        Assert.Equal("reencoded", job.ReencodeResult);
        Assert.Equal("always", job.ToView(DateTimeOffset.UtcNow, svc.Ttl).Reencode);
        Assert.Equal("Re-encoded to H.264/AAC MP4 for iPhone.", job.Notice);
        var stats = svc.GetStats();
        Assert.Equal(1, stats.Reencoded);
        Assert.Equal(0, stats.ReencodeFailed);
    }

    [Fact]
    public async Task Skipped_by_auto_is_reported_but_not_counted_as_an_encode()
    {
        var enc = new Enc((input, _, _, _) => Task.FromResult(new ReencodeResult(input, ReencodeOutcome.Skipped, "Already iPhone-compatible; no re-encoding needed.")));
        var svc = Create(enc);
        var job = await Finished(svc.Start("https://example.com/v", ReencodeMode.Auto)!);
        Assert.Equal("skipped", job.ReencodeResult);
        Assert.Equal("clip.webm", job.FileName);
        Assert.Equal(0, svc.GetStats().Reencoded);
    }

    [Fact]
    public async Task Encoder_failure_falls_back_to_the_original_file_with_a_message()
    {
        var enc = new Enc((_, _, _, _) => throw new ReencodeException("ffmpeg could not convert this file."));
        var svc = Create(enc);
        var job = await Finished(svc.Start("https://example.com/v", ReencodeMode.Always)!);
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal("clip.webm", job.FileName);
        Assert.Equal("failed", job.ReencodeResult);
        Assert.Contains("original file", job.Notice);
        Assert.Equal(1, svc.GetStats().ReencodeFailed);
    }

    [Fact]
    public async Task Encoder_timeout_falls_back_to_the_original_file()
    {
        // 1 minute is the minimum timeout; make the fake observe cancellation by cancelling itself immediately.
        var enc = new Enc((_, _, _, _) => throw new OperationCanceledException());
        var job = await Finished(Create(enc, 1).Start("https://example.com/v", ReencodeMode.Always)!);
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal("failed", job.ReencodeResult);
        Assert.Contains("too long", job.Notice);
        Assert.Equal("clip.webm", job.FileName);
    }

    [Fact]
    public async Task Only_one_encode_runs_at_a_time()
    {
        var running = 0; var maxRunning = 0;
        var enc = new Enc(async (input, _, _, _) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (this) maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(150);
            Interlocked.Decrement(ref running);
            return new ReencodeResult(input, ReencodeOutcome.Reencoded, null);
        });
        var svc = Create(enc);
        var jobs = Enumerable.Range(0, 3).Select(_ => svc.Start("https://example.com/v", ReencodeMode.Always)!).ToArray();
        foreach (var j in jobs) await Finished(j);
        Assert.All(jobs, j => Assert.Equal(JobState.Done, j.State));
        Assert.Equal(1, maxRunning);
        Assert.Equal(3, enc.Calls);
    }
}
