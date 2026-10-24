namespace Aegis.Web.Services;

/// <summary>Strongly typed settings bound from the "Aegis" configuration section.</summary>
public sealed class AegisOptions
{
    public const string Section = "Aegis";

    /// <summary>Shared passcode. Populated from the AEGIS_PASSCODE environment variable, never from files in the repo.</summary>
    public string Passcode { get; set; } = "";

    /// <summary>Optional long-lived token for headless clients (iOS Shortcuts), sent as the X-Aegis-Token header. Populated from AEGIS_API_TOKEN; empty = disabled.</summary>
    public string ApiToken { get; set; } = "";

    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>Optional directory (or binary path) containing ffmpeg; empty means "use PATH".</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>Directory for in-flight downloads. Empty means a folder under the system temp dir.</summary>
    public string WorkDir { get; set; } = "";

    /// <summary>Directory for persistent state (data-protection keys, OneDrive token). Empty means a folder under the user profile.</summary>
    public string StateDir { get; set; } = "";

    /// <summary>Optional Netscape-format cookies.txt exported from your own browser; passed to yt-dlp via --cookies (helps with YouTube bot checks).</summary>
    public string CookiesFile { get; set; } = "";

    public int MaxConcurrentJobs { get; set; } = 2;
    public string MaxFileSize { get; set; } = "1G";
    public int MaxHeight { get; set; } = 720;
    public int JobTimeoutMinutes { get; set; } = 20;
    public int FileTtlMinutes { get; set; } = 15;
    public int LoginAttemptsPerMinute { get; set; } = 5;
    public int RequestsPerMinute { get; set; } = 60;

    public OneDriveOptions OneDrive { get; set; } = new();

    public ReencodeOptions Reencode { get; set; } = new();

    public string ResolvedWorkDir => string.IsNullOrWhiteSpace(WorkDir)
        ? Path.Combine(Path.GetTempPath(), "aegis-work")
        : WorkDir;

    public string ResolvedStateDir => string.IsNullOrWhiteSpace(StateDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aegis")
        : StateDir;
}

public sealed class OneDriveOptions
{
    /// <summary>Entra (Azure AD) application (client) id of a public-client app registration. Empty = feature not configured.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// "consumers" (default) = personal Microsoft accounts (outlook.com, gmail-based, ...) and uses the dedicated
    /// https://www.microsoft.com/link sign-in page. "common"/"organizations" sign in work/school accounts via
    /// https://login.microsoft.com/device; "common" showed an http://localhost response_type error for personal accounts.
    /// </summary>
    public string TenantId { get; set; } = "consumers";

    /// <summary>Folder in the user's OneDrive that receives uploads.</summary>
    public string Folder { get; set; } = "Aegis";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>Settings for the optional "make it iPhone-compatible" step (ffmpeg libx264/AAC re-encode).</summary>
public sealed class ReencodeOptions
{
    /// <summary>Give up (and keep the original file) after this many minutes of encoding.</summary>
    public int TimeoutMinutes { get; set; } = 30;

    /// <summary>Linux niceness for ffmpeg (0 = off). Keeps encodes from starving the web server on a small VM.</summary>
    public int Nice { get; set; } = 10;
}
