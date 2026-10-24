namespace Aegis.Web.Services;

/// <summary>Strongly typed settings bound from the "Aegis" configuration section.</summary>
public sealed class AegisOptions
{
    public const string Section = "Aegis";

    /// <summary>Shared passcode. Populated from the AEGIS_PASSCODE environment variable, never from files in the repo.</summary>
    public string Passcode { get; set; } = "";

    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>Optional directory (or binary path) containing ffmpeg; empty means "use PATH".</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>Directory for in-flight downloads. Empty means a folder under the system temp dir.</summary>
    public string WorkDir { get; set; } = "";

    /// <summary>Directory for persistent state (data-protection keys, OneDrive token). Empty means a folder under the user profile.</summary>
    public string StateDir { get; set; } = "";

    public int MaxConcurrentJobs { get; set; } = 2;
    public string MaxFileSize { get; set; } = "1G";
    public int MaxHeight { get; set; } = 720;
    public int JobTimeoutMinutes { get; set; } = 20;
    public int FileTtlMinutes { get; set; } = 15;
    public int LoginAttemptsPerMinute { get; set; } = 5;
    public int RequestsPerMinute { get; set; } = 60;

    public OneDriveOptions OneDrive { get; set; } = new();

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

    /// <summary>"common" works for both personal and work/school accounts.</summary>
    public string TenantId { get; set; } = "common";

    /// <summary>Folder in the user's OneDrive that receives uploads.</summary>
    public string Folder { get; set; } = "Aegis";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
