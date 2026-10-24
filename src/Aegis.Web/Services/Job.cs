using System.Text.Json.Serialization;

namespace Aegis.Web.Services;

public enum JobState { Queued, Running, Done, Failed }

public enum UploadState { None, Uploading, Done, Failed }

/// <summary>A single download job. Mutated by the worker; read concurrently by the status endpoint.</summary>
public sealed class Job
{
    private readonly object _lock = new();

    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Directory { get; init; }
    public ReencodeMode Reencode { get; init; } = ReencodeMode.Off;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public JobState State { get; private set; } = JobState.Queued;
    public double Percent { get; private set; }
    public string Message { get; private set; } = "Queued";
    public string? FilePath { get; private set; }
    public string? FileName { get; private set; }
    public long? FileSize { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Why it failed, when known: "login_required", "rate_limited", "unsupported". The UI uses it to offer the Settings page.</summary>
    public string? ErrorCode { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>What the re-encode step did: "none" (not requested), "skipped", "remuxed", "audio", "reencoded" or "failed".</summary>
    public string ReencodeResult { get; private set; } = "none";
    /// <summary>A short human message about the re-encode step (success, skip or fallback), shown next to the download.</summary>
    public string? Notice { get; private set; }

    public UploadState OneDrive { get; private set; } = UploadState.None;
    public string? OneDriveMessage { get; private set; }
    public string? OneDriveUrl { get; private set; }

    public void SetRunning() { lock (_lock) { State = JobState.Running; Message = "Starting"; } }

    public void SetProgress(double percent, string? message = null)
    {
        lock (_lock)
        {
            Percent = Math.Clamp(percent, 0, 100);
            if (message is not null) Message = message;
        }
    }

    public void SetReencodeResult(string result, string? notice) { lock (_lock) { ReencodeResult = result; Notice = notice; } }

    public void SetMessage(string message) { lock (_lock) Message = message; }

    public void SetDone(string path)
    {
        lock (_lock)
        {
            State = JobState.Done;
            Percent = 100;
            FilePath = path;
            FileName = Path.GetFileName(path);
            FileSize = new FileInfo(path).Length;
            Message = "Ready";
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void SetFailed(string error, string? code = null)
    {
        lock (_lock)
        {
            State = JobState.Failed;
            Error = error;
            ErrorCode = code;
            Message = "Failed";
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    public bool TryBeginUpload()
    {
        lock (_lock)
        {
            if (State != JobState.Done || OneDrive == UploadState.Uploading) return false;
            OneDrive = UploadState.Uploading;
            OneDriveMessage = "Uploading to OneDrive…";
            OneDriveUrl = null;
            return true;
        }
    }

    public void SetUploadResult(bool ok, string message, string? webUrl = null)
    {
        lock (_lock)
        {
            OneDrive = ok ? UploadState.Done : UploadState.Failed;
            OneDriveMessage = message;
            OneDriveUrl = webUrl;
        }
    }

    public JobView ToView(DateTimeOffset now, TimeSpan ttl)
    {
        lock (_lock)
        {
            DateTimeOffset? expires = FinishedAt?.Add(ttl);
            return new JobView(Id, State.ToString().ToLowerInvariant(), Math.Round(Percent, 1), Message, FileName, FileSize,
                Error, expires, OneDrive.ToString().ToLowerInvariant(), OneDriveMessage, OneDriveUrl,
                Reencode.ToString().ToLowerInvariant(), ReencodeResult, Notice, ErrorCode);
        }
    }
}

/// <summary>JSON shape returned by the status endpoint.</summary>
public sealed record JobView(
    string Id,
    string State,
    double Percent,
    string Message,
    string? FileName,
    long? FileSize,
    string? Error,
    DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("oneDrive")] string OneDrive,
    string? OneDriveMessage,
    string? OneDriveUrl,
    string Reencode,
    string ReencodeResult,
    string? Notice,
    string? ErrorCode = null);
