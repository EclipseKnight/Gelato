using Gelato.Decorators;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gelato.Tests;

public class PrefetchHintTests
{
    private static HttpContext Request(params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        foreach (var (name, value) in headers)
        {
            ctx.Request.Headers.Append(name, value);
        }
        return ctx;
    }

    [Fact]
    public void No_context_is_not_a_prefetch()
    {
        Assert.False(PrefetchHint.IsPrefetch(null));
    }

    [Fact]
    public void A_request_without_the_headers_is_not_a_prefetch()
    {
        Assert.False(PrefetchHint.IsPrefetch(Request(("Accept", "application/json"))));
    }

    [Theory]
    [InlineData("Purpose", "prefetch")]
    [InlineData("Sec-Purpose", "prefetch")]
    [InlineData("sec-purpose", "Prefetch")]
    [InlineData("PURPOSE", "PREFETCH")]
    [InlineData("Sec-Purpose", " prefetch ")]
    public void Either_header_with_the_token_in_any_case_is_a_prefetch(string name, string value)
    {
        Assert.True(PrefetchHint.IsPrefetch(Request((name, value))));
    }

    [Theory]
    [InlineData("prefetch;prerender")]
    [InlineData("prefetch;anonymous-client-ip")]
    [InlineData("other, prefetch")]
    public void The_token_is_found_with_parameters_and_in_lists(string value)
    {
        Assert.True(PrefetchHint.IsPrefetch(Request(("Sec-Purpose", value))));
    }

    [Fact]
    public void The_token_is_found_in_a_second_header_value()
    {
        Assert.True(
            PrefetchHint.IsPrefetch(Request(("Sec-Purpose", "other"), ("Sec-Purpose", "prefetch")))
        );
    }

    [Theory]
    [InlineData("Purpose", "")]
    [InlineData("Purpose", "prefetching")]
    [InlineData("Sec-Purpose", "noprefetch")]
    [InlineData("Sec-Purpose", "prerender")]
    [InlineData("Sec-Purpose", "foo=prefetch")]
    [InlineData("X-Purpose", "prefetch")]
    public void Other_values_and_headers_are_not_a_prefetch(string name, string value)
    {
        Assert.False(PrefetchHint.IsPrefetch(Request((name, value))));
    }
}
