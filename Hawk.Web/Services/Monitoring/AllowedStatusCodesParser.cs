// <file>
// <summary>
// Parses per-monitor additional HTTP success status codes.
// Input format: comma-separated numeric codes (e.g., "404,429,503").
// </summary>
// </file>

using System.Net;

namespace Hawk.Web.Services.Monitoring;

public static class AllowedStatusCodesParser
{
    public static bool TryParse(string? raw, out HashSet<int> codes, out string? error)
    {
        codes = [];
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var tokens = raw
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
            return true;

        foreach (var token in tokens)
        {
            if (!int.TryParse(token, out var code))
            {
                error = $"Invalid status code '{token}'. Use comma-separated numeric codes, e.g. 404,429.";
                return false;
            }

            if (code is < 100 or > 599)
            {
                error = $"Status code '{code}' is out of range. Allowed range is 100..599.";
                return false;
            }

            codes.Add(code);
        }

        return true;
    }

    public static string? Normalize(string? raw)
    {
        if (!TryParse(raw, out var codes, out _))
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

        if (codes.Count == 0)
            return null;

        return string.Join(",", codes.OrderBy(x => x));
    }

    public static bool IsSuccessStatusCode(HttpStatusCode? statusCode, string? additionalAllowedCodes)
    {
        if (statusCode is null)
            return false;

        var code = (int)statusCode.Value;
        if (code is >= 200 and < 300)
            return true;

        if (!TryParse(additionalAllowedCodes, out var extras, out _))
            return false;

        return extras.Contains(code);
    }
}
