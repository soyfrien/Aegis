using System.Text.RegularExpressions;

namespace Aegis.Web.Services;

/// <summary>Pulls the first http(s) link out of whatever a share sheet / Shortcut hands us (a bare URL, or text such as "Look at this https://… wow").</summary>
public static partial class SharedLink
{
    [GeneratedRegex(@"https?://[^\s<>""'`\u2018\u2019\u201C\u201D]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Returns the first valid http(s) URL found in the candidates (checked in the order given), or null.</summary>
    public static string? Extract(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            foreach (Match m in UrlRegex().Matches(candidate))
            {
                var cleaned = TrimTrailingPunctuation(m.Value);
                if (cleaned.Length <= UrlValidator.MaxLength &&
                    Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                    uri.Host.Length > 0)
                {
                    return cleaned;
                }
            }
        }
        return null;
    }

    /// <summary>"(see https://x.com/a)." -> trailing ")" and "." are not part of the link, but balanced parentheses inside it are kept.</summary>
    private static string TrimTrailingPunctuation(string s)
    {
        while (s.Length > 0)
        {
            var last = s[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '*' or ']' or '}' or '>') { s = s[..^1]; continue; }
            if (last == ')' && s.Count(c => c == ')') > s.Count(c => c == '(')) { s = s[..^1]; continue; }
            break;
        }
        return s;
    }
}
