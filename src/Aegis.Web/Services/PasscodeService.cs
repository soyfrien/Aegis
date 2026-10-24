using System.Security.Cryptography;
using System.Text;

namespace Aegis.Web.Services;

/// <summary>Constant-time comparison of a submitted passcode against the configured one.</summary>
public sealed class PasscodeService
{
    private readonly byte[]? _expectedHash;

    public PasscodeService(string? passcode)
    {
        _expectedHash = string.IsNullOrEmpty(passcode) ? null : Hash(passcode);
    }

    /// <summary>False when AEGIS_PASSCODE is not set; in that case nobody can log in.</summary>
    public bool IsConfigured => _expectedHash is not null;

    public bool Verify(string? candidate)
    {
        if (_expectedHash is null || string.IsNullOrEmpty(candidate)) return false;
        return CryptographicOperations.FixedTimeEquals(Hash(candidate), _expectedHash);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
