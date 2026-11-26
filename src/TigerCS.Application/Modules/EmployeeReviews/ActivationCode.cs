using System.Security.Cryptography;
using System.Text;

namespace TigerCS.Application.Modules.EmployeeReviews;

/// <summary>
/// The short code an administrator hands to the person setting up a tablet. It is <b>not</b> the tablet's credential: redeeming it makes the
/// server mint a separate, long <see cref="DeviceToken"/>.
/// <list type="bullet">
/// <item>Exactly <see cref="Length"/> characters, upper-case letters and digits only (no hyphens, no spaces).</item>
/// <item>Drawn from <see cref="Alphabet"/> with a cryptographic RNG, one unbiased draw per character. The alphabet leaves out every
/// character that is easy to misread on a tablet (0/O, 1/I/L, 2/Z, 5/S, 8/B), so 25 symbols remain: 25^8 ≈ 1.5 × 10^11 codes.</item>
/// <item>Only a salted PBKDF2-SHA256 hash is stored. The code is valid for <see cref="Lifetime"/> and one successful use, and guessing is
/// capped by the attempt limits in <c>EmployeeReviewActivationService</c>; the hash is deliberately slow so a copy of the database does
/// not make an unexpired code cheap to recover.</item>
/// </list>
/// </summary>
public static class ActivationCode
{
    public const int Length = 8;

    /// <summary>Upper-case letters and digits without look-alikes: 20 letters (no B, I, L, O, S, Z) and 5 digits (no 0, 1, 2, 5, 8).</summary>
    public const string Alphabet = "ACDEFGHJKMNPQRTUVWXY34679";

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public const int SaltLength = 16;
    public const int HashLength = 32;
    public const int Iterations = 100_000;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltLength);

    /// <summary>The slow, salted hash to store or compare. <paramref name="code"/> must already be normalized and well formed.</summary>
    public static byte[] Hash(string code, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(code), salt, Iterations, HashAlgorithmName.SHA256, HashLength);

    /// <summary>
    /// What an operator types, cleaned up: surrounding whitespace removed and letters upper-cased (a tablet keyboard may start in lower case).
    /// Nothing else is rewritten — a hyphen or an inner space makes the code invalid rather than being quietly dropped.
    /// </summary>
    public static string? Normalize(string? input)
    {
        var trimmed = input?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    /// <summary>True for exactly <see cref="Length"/> characters, every one from <see cref="Alphabet"/>.</summary>
    public static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != Length)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (Alphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool Matches(string code, byte[] salt, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(code, salt), expectedHash);
}
