using Aegis.Web.Services;

namespace Aegis.Tests;

public class SharedLinkTests
{
    [Theory]
    [InlineData("https://www.facebook.com/watch/?v=123", "https://www.facebook.com/watch/?v=123")]
    [InlineData("  https://youtu.be/abc123  ", "https://youtu.be/abc123")]
    [InlineData("Look at this https://youtu.be/abc123 wow", "https://youtu.be/abc123")]
    [InlineData("(see https://example.com/video.mp4).", "https://example.com/video.mp4")]
    [InlineData("check https://example.com/a_(b) now", "https://example.com/a_(b)")]
    [InlineData("\u201Chttps://example.com/x\u201D", "https://example.com/x")]
    [InlineData("first http://a.example/1 then https://b.example/2", "http://a.example/1")]
    public void Extracts_first_link(string input, string expected) => Assert.Equal(expected, SharedLink.Extract(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no links here")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    public void Returns_null_without_http_link(string? input) => Assert.Null(SharedLink.Extract(input));

    [Fact]
    public void Checks_candidates_in_order_and_skips_empty_ones()
    {
        Assert.Equal("https://a.example/", SharedLink.Extract(null, "", "title https://a.example/ x", "https://b.example/"));
        Assert.Equal("https://b.example/", SharedLink.Extract("", "nothing", "https://b.example/"));
    }
}
