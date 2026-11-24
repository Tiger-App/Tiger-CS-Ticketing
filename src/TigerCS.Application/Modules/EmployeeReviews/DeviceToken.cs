using System.Security.Cryptography;
using System.Text;

namespace TigerCS.Application.Modules.EmployeeReviews;

/// <summary>
/// The secret a review tablet holds: 256 bits from a cryptographic RNG, base64url (43 characters), prefixed so an administrator
/// can tell it apart from other secrets. It encodes no employee or device id. Only its SHA-256 hash is stored, and the hash is the
/// lookup key, so a guessed or altered token simply matches nothing.
/// </summary>
public static class DeviceToken
{
    public const string Prefix = "tgr-";
    private const int ByteLength = 32;
    public const int EncodedLength = 43 + 4;

    public static string Generate() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(ByteLength)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The SHA-256 hash to store or look up, or <c>null</c> when <paramref name="token"/> cannot be a token we issued.</summary>
    public static byte[]? Hash(string? token)
    {
        if (!IsWellFormed(token))
        {
            return null;
        }

        return SHA256.HashData(Encoding.ASCII.GetBytes(token!));
    }

    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != EncodedLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in token.AsSpan(Prefix.Length))
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
