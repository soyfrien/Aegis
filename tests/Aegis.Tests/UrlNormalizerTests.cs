using Aegis.Web.Services;

namespace Aegis.Tests;

public class UrlNormalizerTests
{
    private static string Clean(string input)
    {
        Assert.True(UrlValidator.TryValidate(input, out var uri, out var error), error);
        return uri!.AbsoluteUri;
    }

    [Theory]
    // Instagram / X / TikTok / short links: everything after the path goes.
    [InlineData("https://www.instagram.com/reel/C1abcDEF/?igsh=MWxqZ3Zxb2Z4c2R6bQ==", "https://www.instagram.com/reel/C1abcDEF/")]
    [InlineData("https://www.instagram.com/p/C1abcDEF/?utm_source=ig_web_copy_link&igshid=abc", "https://www.instagram.com/p/C1abcDEF/")]
    [InlineData("https://x.com/user/status/123456789?s=20", "https://x.com/user/status/123456789")]
    [InlineData("https://twitter.com/user/status/123456789?s=46&t=AbCdEf", "https://twitter.com/user/status/123456789")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abcDEF123", "https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42&si=abc", "https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/abc123?feature=share", "https://www.youtube.com/shorts/abc123")]
    [InlineData("https://www.tiktok.com/@user/video/7300000000000000000?is_from_webapp=1&sender_device=pc", "https://www.tiktok.com/@user/video/7300000000000000000")]
    [InlineData("https://vm.tiktok.com/ZMabc123/?utm_campaign=x", "https://vm.tiktok.com/ZMabc123/")]
    [InlineData("https://fb.watch/abc123/?mibextid=xyz", "https://fb.watch/abc123/")]
    [InlineData("https://www.facebook.com/reel/123456789?s=fb_shorts_tab&fbclid=IwAR0", "https://www.facebook.com/reel/123456789")]
    [InlineData("https://www.facebook.com/someone/videos/123456789/?idorvanity=5&ref=sharing", "https://www.facebook.com/someone/videos/123456789/")]
    [InlineData("https://www.dailymotion.com/video/x8abc?playlist=x6&utm_medium=a", "https://www.dailymotion.com/video/x8abc")]
    [InlineData("https://example.com/page?foo=bar", "https://example.com/page")]
    public void Strips_the_whole_query_by_default(string input, string expected) => Assert.Equal(expected, Clean(input));

    [Theory]
    // Fragments always go, even when the query is kept.
    [InlineData("https://example.com/video#t=30", "https://example.com/video")]
    [InlineData("https://example.com/video?utm_source=x#section", "https://example.com/video")]
    [InlineData("https://www.youtube.com/watch?v=abc123#t=10s", "https://www.youtube.com/watch?v=abc123")]
    [InlineData("https://www.instagram.com/reel/abc/?igsh=1#frag", "https://www.instagram.com/reel/abc/")]
    public void Strips_fragments(string input, string expected) => Assert.Equal(expected, Clean(input));

    [Theory]
    // YouTube: only v on watch pages (no playlist/time/tracking), list on playlist pages.
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw", "https://www.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PL123&index=3&t=42s&si=abc&feature=shared&pp=xyz", "https://www.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/watch?list=PL123&v=jNQXAC9IVRw", "https://www.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://m.youtube.com/watch?v=jNQXAC9IVRw&si=abc", "https://m.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://music.youtube.com/watch?v=jNQXAC9IVRw&list=RDAMVM", "https://music.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/watch/?v=jNQXAC9IVRw&x=1", "https://www.youtube.com/watch/?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/playlist?list=PLabc123&si=zzz", "https://www.youtube.com/playlist?list=PLabc123")]
    [InlineData("https://www.youtube.com/playlist?si=zzz&list=PLabc123&utm_source=a", "https://www.youtube.com/playlist?list=PLabc123")]
    // Facebook: pages whose query identifies the content.
    [InlineData("https://www.facebook.com/watch/?v=123456789&ref=sharing&mibextid=abc", "https://www.facebook.com/watch/?v=123456789")]
    [InlineData("https://www.facebook.com/watch?v=123456789&t=5", "https://www.facebook.com/watch?v=123456789")]
    [InlineData("https://m.facebook.com/video.php?v=123456789&_rdr", "https://m.facebook.com/video.php?v=123456789")]
    [InlineData("https://www.facebook.com/story.php?story_fbid=111&id=222&mibextid=x&rdid=y", "https://www.facebook.com/story.php?story_fbid=111&id=222")]
    [InlineData("https://www.facebook.com/permalink.php?story_fbid=pfbid0abc&id=222&__cft__[0]=x", "https://www.facebook.com/permalink.php?story_fbid=pfbid0abc&id=222")]
    [InlineData("https://www.facebook.com/photo.php?fbid=333&set=a.1&type=3", "https://www.facebook.com/photo.php?fbid=333")]
    // Vimeo privacy hash and bilibili part number.
    [InlineData("https://player.vimeo.com/video/123456?h=abcdef1234&autoplay=1&utm_source=x", "https://player.vimeo.com/video/123456?h=abcdef1234")]
    [InlineData("https://vimeo.com/123456?share=copy", "https://vimeo.com/123456")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD?p=3&spm_id_from=333", "https://www.bilibili.com/video/BV1xx411c7mD?p=3")]
    public void Keeps_only_the_query_keys_the_host_needs(string input, string expected) => Assert.Equal(expected, Clean(input));

    [Theory]
    // Direct file/stream links lose their whole query and fragment too (signed CDN links will no longer work).
    [InlineData("https://cdn.example.com/clip.mp4?utm_source=x", "https://cdn.example.com/clip.mp4")]
    [InlineData("https://cdn.example.com/clip.mp4?utm_source=x&fbclid=abc", "https://cdn.example.com/clip.mp4")]
    [InlineData("https://cdn.example.com/clip.mp4?token=abc&expires=123&utm_medium=y", "https://cdn.example.com/clip.mp4")]
    [InlineData("https://cdn.example.com/clip.mp4?utm_source=x&token=abc#t=1", "https://cdn.example.com/clip.mp4")]
    [InlineData("https://cdn.example.com/stream/master.m3u8?sig=AbC%3D&gclid=1#t=3", "https://cdn.example.com/stream/master.m3u8")]
    [InlineData("https://cdn.example.com/a/manifest.mpd?token=1", "https://cdn.example.com/a/manifest.mpd")]
    [InlineData("https://cdn.example.com/a/song.mp3?x=1", "https://cdn.example.com/a/song.mp3")]
    [InlineData("https://cdn.example.com/a/clip.webm?x=1", "https://cdn.example.com/a/clip.webm")]
    public void Direct_media_links_lose_the_whole_query(string input, string expected) => Assert.Equal(expected, Clean(input));

    [Theory]
    [InlineData("instagram.com/reel/C1abc/?igsh=abc", "https://instagram.com/reel/C1abc/")]
    [InlineData("www.youtube.com/watch?v=abc123&list=PL1", "https://www.youtube.com/watch?v=abc123")]
    [InlineData("youtu.be/abc123?si=x", "https://youtu.be/abc123")]
    [InlineData("  fb.watch/abc/?mibextid=1  ", "https://fb.watch/abc/")]
    [InlineData("x.com/user/status/1?s=20", "https://x.com/user/status/1")]
    [InlineData("HTTPS://WWW.EXAMPLE.COM/Video?A=1", "https://www.example.com/Video")]
    public void Accepts_urls_without_a_scheme(string input, string expected) => Assert.Equal(expected, Clean(input));

    [Fact]
    public void Preserves_the_order_and_escaping_of_kept_keys_and_drops_duplicates()
    {
        Assert.Equal("https://www.facebook.com/story.php?id=222&story_fbid=111",
            Clean("https://www.facebook.com/story.php?id=222&utm=1&story_fbid=111&id=999"));
        Assert.Equal("https://www.youtube.com/watch?v=a%2Bb", Clean("https://www.youtube.com/watch?v=a%2Bb&x=1"));
    }

    [Fact]
    public void Keeps_path_port_and_userinfo_free_form()
    {
        Assert.Equal("https://example.com:8443/a/b%20c", Clean("https://example.com:8443/a/b%20c?x=1"));
        Assert.Equal("http://example.com/a", Clean("http://example.com:80/a?x=1"));
    }

    [Fact]
    public void Query_with_only_empty_parts_or_a_bare_question_mark_is_removed()
    {
        Assert.Equal("https://example.com/a", Clean("https://example.com/a?"));
        Assert.Equal("https://example.com/a", Clean("https://example.com/a?&&"));
        Assert.Equal("https://example.com/a", Clean("https://example.com/a?#"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/a?x=1")]
    [InlineData("localhost:5000/a?x=1")]
    [InlineData("http://localhost/a?x=1")]
    [InlineData("10.0.0.1/a?x=1")]
    [InlineData("http://[::1]/?a=b")]
    [InlineData("javascript:alert(1)")]
    [InlineData("--exec=x?y=1")]
    public void Invalid_or_internal_input_is_still_rejected(string input) =>
        Assert.False(UrlValidator.TryValidate(input, out _, out _));

    [Theory]
    [InlineData("not a url?x=1", "not a url?x=1")]
    [InlineData("ftp://example.com/a?x=1", "ftp://example.com/a?x=1")]
    [InlineData("", "")]
    public void String_overload_leaves_non_urls_alone(string input, string expected) => Assert.Equal(expected, UrlNormalizer.Normalize(input));

    [Fact]
    public void String_overload_cleans_urls() =>
        Assert.Equal("https://x.com/u/status/1", UrlNormalizer.Normalize("x.com/u/status/1?s=20#a"));

    [Fact]
    public void Normalizing_is_idempotent()
    {
        foreach (var url in new[]
                 {
                     "https://www.youtube.com/watch?v=abc&list=x", "https://www.facebook.com/watch/?v=1&ref=x",
                     "https://cdn.example.com/a.mp4?token=1&utm_source=a", "https://x.com/u/status/1?s=20",
                 })
        {
            var once = UrlNormalizer.Normalize(url);
            Assert.Equal(once, UrlNormalizer.Normalize(once));
        }
    }

    [Fact]
    public void Documented_rules_cover_the_required_hosts()
    {
        var rules = UrlNormalizer.RetainedKeys().ToList();
        Assert.Contains(rules, r => r.Host.Contains("youtube.com") && r.Path == "/watch" && r.Keys.SequenceEqual(["v"]));
        Assert.Contains(rules, r => r.Host.Contains("youtube.com") && r.Path == "/playlist" && r.Keys.SequenceEqual(["list"]));
        Assert.Contains(rules, r => r.Host.Contains("facebook.com") && r.Path == "/story.php" && r.Keys.SequenceEqual(["story_fbid", "id"]));
    }
}
