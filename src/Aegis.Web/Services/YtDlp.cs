using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Aegis.Web.Services;

public interface IDownloader
{
    /// <summary>Downloads <paramref name="url"/> into <paramref name="directory"/> and returns the final file path.</summary>
    Task<string> DownloadAsync(string url, string directory, Action<double, string?> progress, CancellationToken ct);
}

public sealed class DownloadException(string message) : Exception(message);

/// <summary>Runs yt-dlp as a child process (no shell, arguments passed as a list).</summary>
public sealed partial class YtDlpDownloader(IOptions<AegisOptions> options, ILogger<YtDlpDownloader> logger) : IDownloader
{
    private static readonly string[] IgnoredExtensions = [".part", ".ytdl", ".json", ".tmp", ".temp", ".txt", ".vtt", ".jpg", ".png", ".webp"];

    [GeneratedRegex(@"\[download\]\s+(?<pct>\d+(?:\.\d+)?)%")]
    private static partial Regex ProgressRegex();

    /// <summary>Extracts the percentage from a yt-dlp "[download]  42.3% of ..." line.</summary>
    public static bool TryParseProgress(string? line, out double percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(line)) return false;
        var m = ProgressRegex().Match(line);
        return m.Success && double.TryParse(m.Groups["pct"].Value, System.Globalization.CultureInfo.InvariantCulture, out percent);
    }

    /// <summary>Builds the yt-dlp argument list. Public so the exact (security relevant) arguments can be unit tested.</summary>
    public static List<string> BuildArguments(AegisOptions o, string url, string directory)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "--newline",
            "--no-warnings",
            "--no-mtime",
            "--no-part",
            "--ignore-config",
            "--socket-timeout", "30",
            "--max-filesize", o.MaxFileSize,
            // Prefer H.264/AAC in MP4 so the result plays natively on iOS.
            "-S", $"res:{o.MaxHeight},vcodec:h264,acodec:aac,ext:mp4:m4a",
            "--merge-output-format", "mp4",
            "--windows-filenames",
            "--trim-filenames", "120",
            "-P", directory,
            "-o", "%(title).80B [%(id)s].%(ext)s",
        };
        if (!string.IsNullOrWhiteSpace(o.FfmpegPath))
        {
            args.Add("--ffmpeg-location");
            args.Add(o.FfmpegPath);
        }
        args.Add("--");
        args.Add(url);
        return args;
    }

    public async Task<string> DownloadAsync(string url, string directory, Action<double, string?> progress, CancellationToken ct)
    {
        var o = options.Value;
        Directory.CreateDirectory(directory);

        var psi = new ProcessStartInfo(o.YtDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArguments(o, url, directory)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var errors = new Queue<string>();
        var stage = "Downloading";

        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (TryParseProgress(line, out var pct)) progress(pct, stage);
            else if (line.StartsWith("[Merger]") || line.StartsWith("[VideoRemuxer]")) { stage = "Merging audio and video"; progress(99, stage); }
            else if (line.StartsWith("[download] Destination")) { stage = "Downloading"; }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors)
            {
                errors.Enqueue(e.Data);
                while (errors.Count > 8) errors.Dequeue();
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not start yt-dlp at {Path}", o.YtDlpPath);
            throw new DownloadException("The downloader is not available on the server.");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string detail;
            lock (errors) detail = string.Join(" ", errors);
            logger.LogWarning("yt-dlp exited with {Code}: {Detail}", process.ExitCode, detail);
            throw new DownloadException(Summarize(detail));
        }

        var file = Directory.EnumerateFiles(directory)
            .Where(f => !IgnoredExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

        return file?.FullName ?? throw new DownloadException("The download finished but no file was produced.");
    }

    /// <summary>Turns yt-dlp's stderr tail into a short, user facing message.</summary>
    public static string Summarize(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "Download failed.";
        if (stderr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
            return "The site is asking the server to sign in / prove it is not a bot (common for YouTube from datacenter IPs).";
        if (stderr.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return "That URL is not supported.";
        if (stderr.Contains("larger than max-filesize", StringComparison.OrdinalIgnoreCase)) return "The video is larger than the server's size limit.";
        var msg = stderr.Replace("ERROR: ", "", StringComparison.Ordinal).Trim();
        return msg.Length > 300 ? msg[..300] + "…" : msg;
    }
}
