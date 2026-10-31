using System.Security.Cryptography;
using System.Text;

namespace EntraOidcScimLab.Services;

public sealed class CookieSigner(LabConfig config)
{
    public string Sign(string value)
    {
        if (string.IsNullOrWhiteSpace(config.SessionSecret))
            throw new InvalidOperationException("SESSION_SECRET is not configured.");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.SessionSecret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"{value}.{signature}";
    }

    public bool TryVerify(string? signedValue, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(signedValue) || string.IsNullOrWhiteSpace(config.SessionSecret)) return false;

        var split = signedValue.LastIndexOf('.');
        if (split <= 0) return false;

        var raw = signedValue[..split];
        var supplied = signedValue[(split + 1)..];
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.SessionSecret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(supplied);
        if (left.Length != right.Length || !CryptographicOperations.FixedTimeEquals(left, right)) return false;

        value = raw;
        return true;
    }
}
