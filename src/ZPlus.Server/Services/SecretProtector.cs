using System.Security.Cryptography;
using System.Text;

namespace ZPlus.Server.Services;

/// <summary>
/// Encrypts secrets at rest with AES-256-CBC and authenticates them with HMAC-SHA256
/// (encrypt-then-MAC). The 64-byte master key (32 AES + 32 HMAC) lives in a key file
/// OUTSIDE the database, so a stolen zplus.db is useless without zplus.key.
/// </summary>
public class SecretProtector
{
    private const string Prefix = "ZP1$";
    private const int IvSize = 16;
    private const int MacSize = 32;

    private readonly byte[] _aesKey;
    private readonly byte[] _hmacKey;

    private SecretProtector(byte[] masterKey)
    {
        _aesKey = masterKey[..32];
        _hmacKey = masterKey[32..64];
    }

    /// <summary>Loads the master key file, generating a new one on first run.</summary>
    public static SecretProtector LoadOrCreate(string keyPath)
    {
        byte[] masterKey;
        if (File.Exists(keyPath))
        {
            masterKey = File.ReadAllBytes(keyPath);
            if (masterKey.Length != 64)
                throw new InvalidOperationException(
                    $"Key file '{keyPath}' is corrupt (expected 64 bytes, found {masterKey.Length}).");
        }
        else
        {
            masterKey = RandomNumberGenerator.GetBytes(64);
            File.WriteAllBytes(keyPath, masterKey);
        }
        return new SecretProtector(masterKey);
    }

    public string Protect(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.GenerateIV();

        byte[] cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), aes.IV);

        byte[] payload = new byte[IvSize + cipher.Length + MacSize];
        aes.IV.CopyTo(payload, 0);
        cipher.CopyTo(payload, IvSize);
        HMACSHA256.HashData(_hmacKey, payload.AsSpan(0, IvSize + cipher.Length))
            .CopyTo(payload.AsSpan(IvSize + cipher.Length));

        return Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>Returns the plaintext, or null if the value is not a valid protected payload
    /// (wrong format, tampered ciphertext, or wrong key).</summary>
    public string? Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (payload.Length < IvSize + MacSize + 16) return null;

        var authenticated = payload.AsSpan(0, payload.Length - MacSize);
        var mac = payload.AsSpan(payload.Length - MacSize);
        byte[] expectedMac = HMACSHA256.HashData(_hmacKey, authenticated);
        if (!CryptographicOperations.FixedTimeEquals(mac, expectedMac)) return null;

        try
        {
            using var aes = Aes.Create();
            aes.Key = _aesKey;
            byte[] plain = aes.DecryptCbc(
                payload.AsSpan(IvSize, payload.Length - IvSize - MacSize), payload.AsSpan(0, IvSize));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
