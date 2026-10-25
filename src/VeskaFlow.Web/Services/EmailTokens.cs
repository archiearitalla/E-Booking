using System.Security.Cryptography;
using System.Text;

namespace VeskaFlow.Web.Services;

/// <summary>Email confirmation link tokens. Only the SHA-256 of a token is stored; the token itself exists only in the email.</summary>
public static class EmailTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(48);

    /// <summary>A new random token (URL-safe) and the hash to store.</summary>
    public static (string Token, string Hash) NewToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, Hash(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
