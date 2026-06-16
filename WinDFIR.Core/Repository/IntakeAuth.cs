using System.Security.Cryptography;
using System.Text;

namespace WinDFIR.Core.Repository;

/// <summary>
/// Shared-secret authentication for the evidence intake. A single bearer token gates every intake request so
/// only configured collection clients can push into (or read the state of) the case repository. This is the
/// minimum bar for an intake reachable beyond a fully trusted host; pair it with TLS termination (reverse
/// proxy) before exposing it on an untrusted network — the token is a credential and travels in plaintext otherwise.
/// </summary>
public static class IntakeAuth
{
    public const string HeaderName = "Authorization";
    private const string Prefix = "Bearer ";

    /// <summary>Builds the Authorization header value a client sends for <paramref name="token"/>.</summary>
    public static string BuildHeaderValue(string token) => Prefix + token;

    /// <summary>
    /// True if the request may proceed. When <paramref name="expectedToken"/> is null/empty, auth is disabled
    /// and every request is allowed (preserves the unauthenticated trusted-LAN mode). Otherwise the presented
    /// bearer token must match in constant time.
    /// </summary>
    public static bool IsAuthorized(string? authorizationHeader, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
            return true;

        if (string.IsNullOrEmpty(authorizationHeader)
            || !authorizationHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorizationHeader[Prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
