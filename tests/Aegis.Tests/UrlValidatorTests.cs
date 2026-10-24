using Aegis.Web.Services;

namespace Aegis.Tests;

public class UrlValidatorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("  http://example.com/video  ")]
    [InlineData("https://fb.watch/abc123/")]
    public void Accepts_public_http_urls(string input)
    {
        Assert.True(UrlValidator.TryValidate(input, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("--exec=rm -rf /")]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://172.20.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    [InlineData("http://intranet/")]
    public void Rejects_unsafe_or_invalid_input(string? input)
    {
        Assert.False(UrlValidator.TryValidate(input, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Rejects_overlong_urls()
    {
        var url = "https://example.com/" + new string('a', UrlValidator.MaxLength);
        Assert.False(UrlValidator.TryValidate(url, out _, out _));
    }
}
