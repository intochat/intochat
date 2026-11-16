using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests;

// A share names one folder or file of the workspace mount. Every request under a share is resolved here, segment by
// segment, so no spelling of a path reaches anything outside it.
public sealed class SharePathFacts
{
    [Theory]
    [InlineData("/work/trip", "/work/trip")]
    [InlineData("work/trip/", "/work/trip")]
    [InlineData("\\work\\trip", "/work/trip")]
    [InlineData("/work/a/b.md", "/work/a/b.md")]
    public void Normalize_accepts_paths_inside_the_workspace_mount(string raw, string expected)
        => Assert.Equal(expected, SharePath.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("/work")]
    [InlineData("/work/")]
    [InlineData("/Work/trip")]
    [InlineData("/local/trip")]
    [InlineData("/drive/trip")]
    [InlineData("/work/../local/x")]
    [InlineData("/work/a/./b")]
    [InlineData("/work/./a")]
    [InlineData("/work//a")]
    [InlineData("C:/x")]
    [InlineData("/work/C:/x")]
    [InlineData("/work/a\0b")]
    public void Normalize_refuses_everything_else(string raw)
        => Assert.Throws<ArgumentException>(() => SharePath.Normalize(raw));

    [Theory]
    [InlineData("/work/trip", true, "", "/work/trip")]
    [InlineData("/work/trip", true, null, "/work/trip")]
    [InlineData("/work/trip", true, "day1/a.jpg", "/work/trip/day1/a.jpg")]
    [InlineData("/work/trip", true, "day1\\a.jpg", "/work/trip/day1/a.jpg")]
    [InlineData("/work/trip", true, "day1/", "/work/trip/day1")]
    [InlineData("/work/a.md", false, "", "/work/a.md")]
    public void Resolve_stays_inside_the_share(string share, bool folder, string? relative, string expected)
        => Assert.Equal(expected, SharePath.Resolve(share, folder, relative));

    [Theory]
    [InlineData("/work/trip", true, "..")]
    [InlineData("/work/trip", true, "../other")]
    [InlineData("/work/trip", true, "/work/other")]
    [InlineData("/work/trip", true, "/work/trip/day1")]
    [InlineData("/work/trip", true, "day1/../../other")]
    [InlineData("/work/trip", true, "./day1")]
    [InlineData("/work/trip", true, "day1//a")]
    [InlineData("/work/a", true, "../ab/x")]
    [InlineData("/work/trip", true, "/local/x")]
    [InlineData("/work/trip", true, "C:/x")]
    [InlineData("/work/trip", true, "a\0b")]
    [InlineData("/work/a.md", false, "x")]
    public void Resolve_refuses_a_path_outside_the_share(string share, bool folder, string relative)
        => Assert.Throws<ArgumentException>(() => SharePath.Resolve(share, folder, relative));

    [Fact]
    public void A_sibling_whose_name_starts_with_the_shares_name_is_not_inside_it()
    {
        Assert.False(SharePath.IsInside("/work/a", "/work/ab/x"));
        Assert.True(SharePath.IsInside("/work/a", "/work/a/x"));
        Assert.True(SharePath.IsInside("/work/a", "/work/a"));
        Assert.False(SharePath.IsInside("/work/a/x", "/work/a"));
    }
}
