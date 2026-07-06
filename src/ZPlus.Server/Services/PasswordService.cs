using Microsoft.AspNetCore.Identity;

namespace ZPlus.Server.Services;

/// <summary>
/// Stores passwords as PBKDF2 hashes wrapped in an AES-256 + HMAC-SHA256 envelope:
/// the password is first one-way hashed (so it can never be recovered), then the hash is
/// encrypted and authenticated with the server's key file before being written to SQLite.
/// </summary>
public class PasswordService(SecretProtector protector)
{
    private readonly PasswordHasher<object> _hasher = new();

    public string Protect(string password) =>
        protector.Protect(_hasher.HashPassword(null!, password));

    public bool Verify(string stored, string password)
    {
        // Values written before encryption-at-rest was added are plain PBKDF2 hashes.
        var hash = protector.Unprotect(stored) ?? stored;
        return _hasher.VerifyHashedPassword(null!, hash, password) != PasswordVerificationResult.Failed;
    }
}
