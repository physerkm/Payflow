using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PayFlow.Infrastructure.PaymentProviders;

/// <summary>
/// HMAC-SHA256 webhook signatures in the form <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c> over <c>"{t}.{body}"</c>.
/// The timestamp is part of the signed content, which gives replay protection together with a tolerance window.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "PayFlow-Signature";
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string Create(string secret, DateTimeOffset timestamp, string body)
    {
        var t = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Compute(secret, t, body)}";
    }

    public static bool Verify(string? header, string body, string secret, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        string? timestamp = null;
        string? signature = null;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            if (kv[0] == "t")
            {
                timestamp = kv[1];
            }
            else if (kv[0] == "v1")
            {
                signature = kv[1];
            }
        }

        if (timestamp is null || signature is null || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            return false;
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        if ((now - sentAt).Duration() > (tolerance ?? DefaultTolerance))
        {
            return false; // replay or badly skewed clock
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp, body));
        var actual = Encoding.ASCII.GetBytes(signature.ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Compute(string secret, string timestamp, string body)
    {
        var payload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
    }
}
