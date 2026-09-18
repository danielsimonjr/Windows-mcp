using FluentAssertions;
using WindowsMcp.Services;

namespace WindowsMcp.Tests;

/// <summary>
/// Ranking rules for window lookup. These encode the real failure that prompted the change:
/// `focus` could not find a window that `get_state` listed, because the old path required an
/// exact full-title match through FindWindow.
/// </summary>
public class WindowMatcherTests
{
    private static WindowCandidate Win(nint h, string title, bool visible = true, long area = 1000)
        => new(h, title, visible, area);

    [Fact]
    public void Finds_a_window_by_substring_which_is_how_a_caller_names_an_app()
    {
        // The measured case: the caption is decorated with live state, the caller says "Obsidian".
        var windows = new[] { Win(1, "New tab - Vault-mcp-test - Obsidian 1.13.7") };

        WindowMatcher.Select(windows, "Obsidian")!.Value.Handle.Should().Be(1);
    }

    [Fact]
    public void Still_finds_a_window_by_its_exact_full_title()
    {
        var windows = new[] { Win(7, "New tab - Vault-mcp-test - Obsidian 1.13.7") };

        WindowMatcher.Select(windows, "New tab - Vault-mcp-test - Obsidian 1.13.7")!
            .Value.Handle.Should().Be(7);
    }

    [Fact]
    public void Exact_match_outranks_a_substring_match()
    {
        var windows = new[]
        {
            Win(1, "Settings - Obsidian"),
            Win(2, "Settings"),
        };

        WindowMatcher.Select(windows, "Settings")!.Value.Handle.Should().Be(2);
    }

    [Fact]
    public void Prefix_outranks_substring()
    {
        var windows = new[]
        {
            Win(1, "Editing - Notepad"),
            Win(2, "Notepad helper"),
        };

        WindowMatcher.Select(windows, "Notepad")!.Value.Handle.Should().Be(2);
    }

    [Fact]
    public void A_visible_window_beats_a_hidden_one_with_the_same_caption()
    {
        // File Explorer and the PortableApps platform both keep offscreen helper windows carrying
        // the real window's caption; activating one of those is a silent no-op.
        var windows = new[]
        {
            Win(1, "PortableApps.com Platform", visible: false, area: 0),
            Win(2, "PortableApps.com Platform", visible: true, area: 500_000),
        };

        WindowMatcher.Select(windows, "PortableApps.com Platform")!.Value.Handle.Should().Be(2);
    }

    [Fact]
    public void Between_two_visible_matches_the_larger_window_wins()
    {
        var windows = new[]
        {
            Win(1, "Chrome", area: 10),
            Win(2, "Chrome", area: 2_000_000),
        };

        WindowMatcher.Select(windows, "Chrome")!.Value.Handle.Should().Be(2);
    }

    [Fact]
    public void Matching_ignores_case_and_surrounding_whitespace()
    {
        var windows = new[] { Win(3, "  Obsidian 1.13.7  ") };

        WindowMatcher.Select(windows, "obsidian")!.Value.Handle.Should().Be(3);
    }

    [Fact]
    public void Returns_null_when_nothing_matches_rather_than_guessing()
    {
        var windows = new[] { Win(1, "Calculator"), Win(2, "Notepad") };

        WindowMatcher.Select(windows, "Obsidian").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_query_matches_nothing_and_never_returns_an_arbitrary_window(string query)
    {
        var windows = new[] { Win(1, "Calculator") };

        WindowMatcher.Select(windows, query).Should().BeNull();
    }

    [Fact]
    public void Untitled_windows_are_skipped()
    {
        var windows = new[] { Win(1, ""), Win(2, "Obsidian") };

        WindowMatcher.Select(windows, "Obsidian")!.Value.Handle.Should().Be(2);
    }
}
