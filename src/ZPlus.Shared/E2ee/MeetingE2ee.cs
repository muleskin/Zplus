using System.Security.Cryptography;
using System.Text;

namespace ZPlus.Shared.E2ee;

/// <summary>
/// End-to-end encryption for meeting chat. Each participant holds an ephemeral ECDH
/// (P-256) key pair; the meeting-key holder wraps a random AES-256 meeting key for each
/// joiner using the ECDH shared secret, and all chat is AES-GCM encrypted with that key.
/// The server only ever relays public keys, wrapped keys and ciphertext — it cannot
/// read messages. Signal types: "e2ee-pub" (public key), "e2ee-key" (wrapped key).
/// </summary>
public sealed class MeetingE2ee : IDisposable
{
    /// <summary>Prefix marking an end-to-end encrypted chat payload.</summary>
    public const string Prefix = "ZE1$";
    public const string SignalPublicKey = "e2ee-pub";
    public const string SignalWrappedKey = "e2ee-key";

    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private byte[]? _meetingKey;

    public bool HasKey => _meetingKey is not null;

    public string PublicKeyBase64 => Convert.ToBase64String(_ecdh.PublicKey.ExportSubjectPublicKeyInfo());

    /// <summary>Called by the first participant in the room: mints the meeting key.</summary>
    public void CreateMeetingKey() => _meetingKey ??= RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Key-holder side: wraps the meeting key for a peer. The payload carries our public
    /// key so the peer can derive the same ECDH secret. Format: pub:nonce:cipher:tag (base64).
    /// </summary>
    public string WrapKeyFor(string peerPublicKeyBase64)
    {
        if (_meetingKey is null) throw new InvalidOperationException("No meeting key to wrap.");
        byte[] shared = DeriveShared(peerPublicKeyBase64);

        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[_meetingKey.Length];
        byte[] tag = new byte[16];
        using var gcm = new AesGcm(shared, 16);
        gcm.Encrypt(nonce, _meetingKey, cipher, tag);

        return string.Join(':',
            PublicKeyBase64, Convert.ToBase64String(nonce),
            Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
    }

    /// <summary>Joiner side: unwraps a meeting key wrapped for our public key.</summary>
    public bool TryUnwrapKey(string payload)
    {
        try
        {
            var parts = payload.Split(':');
            if (parts.Length != 4) return false;
            byte[] shared = DeriveShared(parts[0]);
            byte[] nonce = Convert.FromBase64String(parts[1]);
            byte[] cipher = Convert.FromBase64String(parts[2]);
            byte[] tag = Convert.FromBase64String(parts[3]);

            byte[] key = new byte[cipher.Length];
            using var gcm = new AesGcm(shared, 16);
            gcm.Decrypt(nonce, cipher, tag, key);
            _meetingKey = key;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Encrypts a chat message with the meeting key: "ZE1$" + base64(nonce|cipher|tag).</summary>
    public string Encrypt(string plaintext)
    {
        if (_meetingKey is null) throw new InvalidOperationException("No meeting key.");
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] payload = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
        using var gcm = new AesGcm(_meetingKey, 16);
        gcm.Encrypt(payload.AsSpan(0, 12), plain, payload.AsSpan(12, plain.Length), payload.AsSpan(12 + plain.Length, 16));
        return Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>True if the message is an encrypted payload (whether or not we can decrypt it yet).</summary>
    public static bool IsEncrypted(string message) => message.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Decrypts an encrypted chat payload. False if not encrypted, no key, or tampered.</summary>
    public bool TryDecrypt(string message, out string plaintext)
    {
        plaintext = "";
        if (!IsEncrypted(message) || _meetingKey is null) return false;
        try
        {
            byte[] payload = Convert.FromBase64String(message[Prefix.Length..]);
            if (payload.Length < 12 + 16) return false;
            byte[] plain = new byte[payload.Length - 12 - 16];
            using var gcm = new AesGcm(_meetingKey, 16);
            gcm.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(12, plain.Length),
                payload.AsSpan(12 + plain.Length, 16), plain);
            plaintext = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private byte[] DeriveShared(string peerPublicKeyBase64)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPublicKeyBase64), out _);
        return _ecdh.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
    }

    public void Dispose() => _ecdh.Dispose();
}
