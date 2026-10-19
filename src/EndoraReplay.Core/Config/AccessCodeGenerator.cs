using System.Security.Cryptography;

namespace EndoraReplay.Core.Remote;

public static class AccessCodeGenerator
{
    // No 0/O/1/I so codes can be read aloud.
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>Creates a code like "7KQ2-M9XA" (40 bits of entropy).</summary>
    public static string Create()
    {
        Span<char> chars = stackalloc char[9];
        for (var i = 0; i < 9; i++)
            chars[i] = i == 4 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Constant-time comparison so the code cannot be guessed by timing.</summary>
    public static bool Matches(string expected, string? provided)
    {
        if (provided is null) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(expected.ToUpperInvariant());
        var b = System.Text.Encoding.UTF8.GetBytes(provided.Trim().ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
