using System.Security.Cryptography;

namespace ZPlus.Server.Services;

public static class MeetingCodeGenerator
{
    /// <summary>Generates a Zoom-style 9-digit meeting code formatted as "###-###-###".</summary>
    public static string NewCode()
    {
        Span<char> digits = stackalloc char[9];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }
        // Avoid a leading zero so codes are always 9 significant digits.
        if (digits[0] == '0') digits[0] = (char)('1' + RandomNumberGenerator.GetInt32(9));
        return $"{new string(digits[..3])}-{new string(digits[3..6])}-{new string(digits[6..])}";
    }
}
