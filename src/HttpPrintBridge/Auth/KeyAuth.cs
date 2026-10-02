using System.Security.Cryptography;
using System.Text;

namespace HttpPrintBridge.Auth;

/// <summary>
/// Path-segment key authentication. The expected key is hashed once; incoming keys are
/// hashed and compared with a fixed-time comparison so timing does not leak the secret.
/// </summary>
public sealed class KeyAuth
{
    private readonly byte[] _expectedHash;

    public KeyAuth(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Auth key must not be empty.", nameof(key));

        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    public bool IsMatch(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        byte[] candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(candidateHash, _expectedHash);
    }
}
