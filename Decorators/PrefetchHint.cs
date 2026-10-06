using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Gelato.Decorators;

/// <summary>
/// Reads the standard prefetch hint of a request. Browsers send <c>Sec-Purpose: prefetch</c>
/// (older ones <c>Purpose: prefetch</c>) for a speculative load, and clients send the same when
/// they warm a title's data before anyone opens it, say when a card is hovered or focused. Such a
/// read is served as usual, but work meant for a real page open (the pre-probe) is left out.
/// </summary>
public static class PrefetchHint
{
    private static readonly string[] Headers = ["Sec-Purpose", "Purpose"];

    /// <summary>
    /// Whether the request says it is a prefetch: a <c>Sec-Purpose</c> or <c>Purpose</c> header
    /// that holds the token <c>prefetch</c>, in any case. <c>Sec-Purpose</c> is a list whose items
    /// may carry parameters (<c>prefetch;prerender</c>), so the value is split on commas and
    /// semicolons and each part is compared on its own.
    /// </summary>
    public static bool IsPrefetch(HttpContext? ctx)
    {
        if (ctx is null)
            return false;

        foreach (var name in Headers)
        {
            if (
                ctx.Request.Headers.TryGetValue(name, out StringValues values)
                && values.Any(HasPrefetchToken)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPrefetchToken(string? value) =>
        value is not null
        && value
            .Split(
                [',', ';'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
            .Contains("prefetch", StringComparer.OrdinalIgnoreCase);
}
