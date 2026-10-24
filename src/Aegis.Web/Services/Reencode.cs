using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aegis.Web.Services;

/// <summary>User choice for making a download playable on an iPhone.</summary>
public enum ReencodeMode { Off, Auto, Always }

/// <summary>What the "Auto" probe decided has to happen to the file.</summary>
public enum ReencodeAction
{
    /// <summary>Leave the file alone.</summary>
    None,
    /// <summary>Codecs are fine, only the container is wrong: copy both streams into an MP4 (seconds, almost no CPU).</summary>
    Remux,
    /// <summary>Video is fine, audio is not AAC: copy the video, encode the audio.</summary>
    ConvertAudio,
    /// <summary>Full H.264/AAC re-encode.</summary>
    Transcode,
}

public enum ReencodeOutcome { Skipped, Remuxed, AudioConverted, Reencoded }

public sealed record ReencodeResult(string Path, ReencodeOutcome Outcome, string? Notice);

public sealed class ReencodeException(string message) : Exception(message);

/// <summary>What ffprobe reported about the downloaded file.</summary>
public sealed record MediaInfo(string? Container, string? Extension, string? VideoCodec, string? PixelFormat, string? VideoTag, string? AudioCodec, double? DurationSeconds);

public interface IReencoder
{
    /// <summary>Probes <paramref name="inputPath"/> and converts it according to <paramref name="mode"/>. Throws <see cref="ReencodeException"/> on failure (the caller keeps the original).</summary>
    Task<ReencodeResult> ProcessAsync(string inputPath, ReencodeMode mode, Action<double, string?> progress, CancellationToken ct);
}

public static class ReencodeModes
{
    /// <summary>Parses "off" / "auto" / "always" (case-insensitive). Null or blank means Off; anything else is invalid.</summary>
    public static bool TryParse(string? value, out ReencodeMode mode)
    {
        mode = ReencodeMode.Off;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "off": mode = ReencodeMode.Off; return true;
            case "auto": mode = ReencodeMode.Auto; return true;
            case "always": mode = ReencodeMode.Always; return true;
            default: return false;
        }
    }
}

/// <summary>ffprobe + ffmpeg driver (child processes, no shell, arguments passed as a list).</summary>
public sealed class FfmpegReencoder(IOptions<AegisOptions> options, ILogger<FfmpegReencoder> logger) : IReencoder
{
    private static readonly string[] OkContainerExtensions = [".mp4", ".m4v", ".mov"];

    // Fit inside 1920x1080 (either orientation), never upscale, keep both sides even (required by yuv420p).
    // ffmpeg auto-rotates before this filter runs, so iw/ih are the displayed dimensions.
    private const string ScaleFilter =
        "scale=w='trunc(iw*min(1,min(1920/max(iw,ih),1080/min(iw,ih)))/2)*2':h='trunc(ih*min(1,min(1920/max(iw,ih),1080/min(iw,ih)))/2)*2'";

    // ---- pure logic (unit tested) ----

    /// <summary>Parses ffprobe's JSON (<c>-show_format -show_streams</c>) into the few facts the decision needs.</summary>
    public static MediaInfo? ParseProbe(string json, string? path = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? container = null; double? duration = null;
            if (root.TryGetProperty("format", out var fmt))
            {
                container = Str(fmt, "format_name");
                if (Str(fmt, "duration") is { } d && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0) duration = secs;
            }

            JsonElement? video = null, audio = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type");
                    var codec = Str(s, "codec_name");
                    if (type == "video" && video is null && codec is not ("mjpeg" or "png" or "bmp")) video = s; // skip cover art
                    else if (type == "audio" && audio is null) audio = s;
                }
            }
            return new MediaInfo(container, path is null ? null : Path.GetExtension(path).ToLowerInvariant(),
                video is { } v ? Str(v, "codec_name") : null, video is { } v2 ? Str(v2, "pix_fmt") : null, video is { } v3 ? Str(v3, "codec_tag_string") : null,
                audio is { } a ? Str(a, "codec_name") : null, duration);
        }
        catch (JsonException) { return null; }

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>Decides what to do with a file. Auto only touches files an iPhone can't play; the cheapest fix wins.</summary>
    public static ReencodeAction Decide(ReencodeMode mode, MediaInfo? info)
    {
        if (mode == ReencodeMode.Off) return ReencodeAction.None;
        if (info is { VideoCodec: null }) return ReencodeAction.None; // audio-only download: nothing to re-encode
        if (mode == ReencodeMode.Always) return ReencodeAction.Transcode;
        if (info is null) return ReencodeAction.None; // could not inspect: keep the original (the caller says so)

        var videoOk = IsIosVideo(info);
        var audioOk = info.AudioCodec is null or "aac";
        var containerOk = info.Container is { } c && c.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                          && info.Extension is { } e && OkContainerExtensions.Contains(e);
        if (!videoOk) return ReencodeAction.Transcode;
        if (!audioOk) return ReencodeAction.ConvertAudio;
        if (!containerOk || NeedsHvc1Tag(info)) return ReencodeAction.Remux;
        return ReencodeAction.None;
    }

    /// <summary>8-bit 4:2:0 H.264, or HEVC (8/10-bit 4:2:0): the video formats iPhones decode in hardware.</summary>
    public static bool IsIosVideo(MediaInfo info) => info.VideoCodec switch
    {
        "h264" => info.PixelFormat is null or "yuv420p" or "yuvj420p",
        "hevc" => info.PixelFormat is null or "yuv420p" or "yuv420p10le",
        _ => false,
    };

    /// <summary>iOS only plays HEVC in MP4 when the sample entry is "hvc1" (not "hev1").</summary>
    public static bool NeedsHvc1Tag(MediaInfo info) => info.VideoCodec == "hevc" && !string.Equals(info.VideoTag, "hvc1", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ffmpeg arguments for an action (everything after the program name).</summary>
    public static List<string> BuildArguments(ReencodeAction action, string input, string output, bool hvc1Tag = false, int threads = 2)
    {
        if (action == ReencodeAction.None) throw new ArgumentException("Nothing to build for ReencodeAction.None", nameof(action));
        var a = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-i", input,
            "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-map_metadata", "0", "-map_chapters", "-1",
        };
        switch (action)
        {
            case ReencodeAction.Remux:
                a.AddRange(["-c", "copy"]);
                break;
            case ReencodeAction.ConvertAudio:
                a.AddRange(["-c:v", "copy"]);
                a.AddRange(AudioArgs());
                break;
            default:
                a.AddRange(["-vf", ScaleFilter + ",format=yuv420p"]);
                a.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "high", "-pix_fmt", "yuv420p",
                            "-threads", threads.ToString(CultureInfo.InvariantCulture)]);
                a.AddRange(AudioArgs());
                break;
        }
        if (hvc1Tag && action != ReencodeAction.Transcode) a.AddRange(["-tag:v", "hvc1"]);
        a.AddRange(["-max_muxing_queue_size", "1024", "-movflags", "+faststart", "-f", "mp4", output]);
        return a;

        static string[] AudioArgs() => ["-c:a", "aac", "-b:a", "160k", "-ac", "2"];
    }

    /// <summary>Parses one "-progress" line (out_time_us=… / out_time_ms=…, both in microseconds) into a percentage of <paramref name="durationSeconds"/>.</summary>
    public static bool TryParseProgress(string? line, double? durationSeconds, out double percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(line) || durationSeconds is not > 0) return false;
        const string us = "out_time_us=", ms = "out_time_ms=";
        string? raw = line.StartsWith(us, StringComparison.Ordinal) ? line[us.Length..] : line.StartsWith(ms, StringComparison.Ordinal) ? line[ms.Length..] : null;
        if (raw is null || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) || micros < 0) return false;
        percent = Math.Clamp(micros / 1_000_000.0 / durationSeconds.Value * 100.0, 0, 99);
        return true;
    }

    /// <summary>Resolves ffmpeg/ffprobe from <see cref="AegisOptions.FfmpegPath"/> (empty = PATH, a directory, or the ffmpeg binary itself).</summary>
    public static string ResolveTool(AegisOptions o, string tool)
    {
        var p = o.FfmpegPath;
        if (string.IsNullOrWhiteSpace(p)) return tool;
        if (Directory.Exists(p)) return Path.Combine(p, tool);
        var dir = Path.GetDirectoryName(p);
        return string.IsNullOrEmpty(dir) ? tool : Path.Combine(dir, tool);
    }

    /// <summary>The program and argument list to start: wrapped in <c>nice</c> on Linux so encodes never starve the web server.</summary>
    public static (string File, List<string> Args) WithNice(string program, List<string> args, int nice, bool isLinux)
    {
        if (!isLinux || nice <= 0) return (program, args);
        var wrapped = new List<string> { "-n", nice.ToString(CultureInfo.InvariantCulture), program };
        wrapped.AddRange(args);
        return ("nice", wrapped);
    }

    // ---- execution ----

    public async Task<ReencodeResult> ProcessAsync(string inputPath, ReencodeMode mode, Action<double, string?> progress, CancellationToken ct)
    {
        var o = options.Value;
        if (mode == ReencodeMode.Off) return new ReencodeResult(inputPath, ReencodeOutcome.Skipped, null);

        var info = await ProbeAsync(o, inputPath, ct);
        var action = Decide(mode, info);
        if (action == ReencodeAction.None)
        {
            var why = info is null ? "I couldn't inspect the file, so I left it as it was."
                : info.VideoCodec is null ? "There is no video track to convert."
                : "Already iPhone-compatible; no re-encoding needed.";
            return new ReencodeResult(inputPath, ReencodeOutcome.Skipped, why);
        }

        var dir = Path.GetDirectoryName(inputPath)!;
        var finalPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(inputPath) + ".mp4");
        var temp = Path.Combine(dir, "aegis-encode-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        progress(0, action switch { ReencodeAction.Transcode => "Re-encoding", ReencodeAction.ConvertAudio => "Converting audio", _ => "Repackaging as MP4" });

        try
        {
            await RunFfmpegAsync(o, BuildArguments(action, inputPath, temp, info is not null && NeedsHvc1Tag(info), Math.Min(2, Environment.ProcessorCount)),
                info?.DurationSeconds, progress, ct);
            if (!File.Exists(temp) || new FileInfo(temp).Length < 1024) throw new ReencodeException("ffmpeg produced no usable output.");

            File.Delete(inputPath);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(temp, finalPath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }

        var outcome = action switch { ReencodeAction.Remux => ReencodeOutcome.Remuxed, ReencodeAction.ConvertAudio => ReencodeOutcome.AudioConverted, _ => ReencodeOutcome.Reencoded };
        var notice = outcome switch
        {
            ReencodeOutcome.Remuxed => "Repackaged as MP4 so it plays on iPhone.",
            ReencodeOutcome.AudioConverted => "Audio converted to AAC so it plays on iPhone.",
            _ => "Re-encoded to H.264/AAC MP4 for iPhone.",
        };
        return new ReencodeResult(finalPath, outcome, notice);
    }

    private async Task<MediaInfo?> ProbeAsync(AegisOptions o, string path, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-v", "error", "-print_format", "json", "-show_entries",
            "format=format_name,duration:stream=codec_type,codec_name,pix_fmt,codec_tag_string", "-i", path,
        };
        var psi = NewStartInfo(ResolveTool(o, "ffprobe"), args);
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("no process");
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(probeCts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { /* gone */ } throw; }
            return p.ExitCode == 0 ? ParseProbe(await stdout, path) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ffprobe failed for {File}", Path.GetFileName(path));
            return null;
        }
    }

    private async Task RunFfmpegAsync(AegisOptions o, List<string> args, double? duration, Action<double, string?> progress, CancellationToken ct)
    {
        var (file, finalArgs) = WithNice(ResolveTool(o, "ffmpeg"), args, o.Reencode.Nice, OperatingSystem.IsLinux());
        var psi = NewStartInfo(file, finalArgs);
        using var process = new Process { StartInfo = psi };
        var errors = new Queue<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (TryParseProgress(e.Data, duration, out var pct)) progress(pct, null);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors) { errors.Enqueue(e.Data); while (errors.Count > 8) errors.Dequeue(); }
        };

        try { process.Start(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not start ffmpeg ({File})", file);
            throw new ReencodeException("ffmpeg is not available on the server.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string detail; lock (errors) detail = string.Join(" ", errors);
            logger.LogWarning("ffmpeg exited with {Code}: {Detail}", process.ExitCode, detail);
            throw new ReencodeException("ffmpeg could not convert this file.");
        }
    }

    private static ProcessStartInfo NewStartInfo(string file, List<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
