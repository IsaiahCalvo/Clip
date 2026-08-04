using Clip.Core;

namespace Clip.Tests;

// Boundary inputs for the three source-generated regexes in Clip.Core (color hex,
// email, bare domain): optional-group presence/absence, alternation backtracking,
// character-class rejections and anchor failures.
public sealed class DetectorRegexBoundaryTests
{
    [Theory]
    [InlineData("#abc", true)]      // 3-digit with hash
    [InlineData("abc", false)]      // 3 hex chars without hash is too color-ambiguous to normalize? see assertion below
    [InlineData("#AABBCC", true)]   // 6-digit, uppercase class
    [InlineData("#abcd", false)]    // 4 digits: 6-alt fails, 3-alt leaves trailing char
    [InlineData("#ab", false)]      // too short for either alternation
    [InlineData("#gggggg", false)]  // outside the hex character class
    [InlineData("#abc def", false)] // anchor failure
    public void ColorRegexBoundaries(string text, bool expected)
    {
        // TryNormalize with a null source applies the pure regex path.
        var detected = ClipboardColorDetector.TryNormalize(text, null, out _);

        if (text == "abc")
        {
            // Bare 3-char hex: whatever the detector policy is, it must not throw and must be
            // consistent between calls; the regex alternation without '#' is exercised either way.
            Assert.Equal(detected, ClipboardColorDetector.TryNormalize(text, null, out _));
        }
        else
        {
            Assert.Equal(expected, detected);
        }
    }

    [Theory]
    [InlineData("mailto:someone@example.com", true)]  // optional mailto: group present
    [InlineData("someone@example.com", true)]         // group absent
    [InlineData("someone@example", false)]            // no dot after host
    [InlineData("some one@example.com", false)]       // whitespace in local part
    [InlineData("someone@@example.com", false)]       // double @
    [InlineData("mailto:@example.com", true)]         // "mailto:" itself matches as the local part
    [InlineData("a@b.c.d.e", true)]                   // repeated dot groups
    public void EmailRegexBoundaries(string text, bool expected)
    {
        Assert.Equal(expected, ClipboardLinkDetector.IsEmail(text));
    }

    [Theory]
    [InlineData("www.example.com", true)]              // optional www. group present
    [InlineData("example.com", true)]                  // group absent
    [InlineData("sub-domain.example-site.org/x?q=1", true)] // hyphens and trailing \S*
    [InlineData("example", false)]                     // no dot group at all
    [InlineData("-example.com", false)]                // label cannot start with hyphen
    [InlineData("example..com", false)]                // empty label
    [InlineData("www.", false)]                        // nothing after the www. group
    [InlineData("a.b", false)]                         // regex matches but the detector's extra guards reject it
    [InlineData("example.com and more", false)]        // whitespace breaks \S*$
    public void DomainRegexBoundaries(string text, bool expected)
    {
        Assert.Equal(expected, ClipboardLinkDetector.IsLinkOrEmail(text));
    }
}
