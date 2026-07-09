using System.Security.Cryptography;
using System.Text;

namespace ZPlus.Server.Services;

/// <summary>
/// Time-based one-time passwords (RFC 6238, HMAC-SHA1, 6 digits, 30-second step) for MFA.
/// Secrets are base32 strings compatible with Google Authenticator, Authy, 1Password, etc.
/// </summary>
public class TotpService
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Generates a new random base32 secret (160 bits).</summary>
    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encode(bytes);
    }

    /// <summary>Builds the otpauth:// URI an authenticator app scans or imports.</summary>
    public string BuildOtpauthUri(string secret, string issuer, string account)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var iss = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={iss}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>Verifies a code, allowing ±1 step of clock drift.</summary>
    public bool Verify(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim();
        if (code.Length != Digits) return false;

        byte[] key;
        try { key = Base32Decode(secret); }
        catch { return false; }

        long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
        for (long offset = -1; offset <= 1; offset++)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Compute(key, counter + offset)),
                    Encoding.ASCII.GetBytes(code)))
                return true;
        }
        return false;
    }

    private static string Compute(byte[] key, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);
        int otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString().PadLeft(Digits, '0');
    }

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        input = input.TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var output = new List<byte>(input.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var c in input)
        {
            int idx = Base32Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException("Invalid base32 character.");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
