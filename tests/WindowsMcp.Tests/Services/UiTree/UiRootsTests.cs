using System.ComponentModel;
using FluentAssertions;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// D-10 R24b: <c>GetForegroundRoot</c> documents "foreground window, else focused element, else
/// desktop", but <c>_automation.FocusedElement()</c> THROWS (<c>Win32Exception: Unexpected
/// HRESULT</c>) when nothing is foreground, so <c>get_state</c>, a foreground <c>find_element</c>
/// and a foreground <c>snapshot</c> failed masked instead of falling back to the desktop.
/// <c>UiRoots.FirstAvailable</c> is that ladder without UIA: the first candidate that answers
/// non-null without throwing wins; the last one is the unconditional fallback.
/// A desktop with nothing in the foreground cannot be arranged headless, so this is the proof.
/// </summary>
[Trait("Category", "Unit")]
public class UiRootsTests
{
    private sealed class Root(string name)
    {
        public string Name { get; } = name;
    }

    private static readonly Root Foreground = new("foreground");
    private static readonly Root Focused = new("focused");
    private static readonly Root Desktop = new("desktop");

    [Fact]
    public void The_first_candidate_that_answers_wins_and_the_rest_are_never_asked()
    {
        var asked = new List<string>();

        var root = UiRoots.FirstAvailable<Root>(
            () => { asked.Add("foreground"); return Foreground; },
            () => { asked.Add("focused"); return Focused; },
            () => { asked.Add("desktop"); return Desktop; });

        root.Should().BeSameAs(Foreground);
        asked.Should().Equal(["foreground"], "every candidate is a cross-process UIA call");
    }

    [Fact]
    public void A_candidate_that_returns_null_falls_through_to_the_next()
    {
        var root = UiRoots.FirstAvailable<Root>(() => null, () => Focused, () => Desktop);

        root.Should().BeSameAs(Focused);
    }

    public static TheoryData<Exception> CandidateFailures => new()
    {
        new Win32Exception(),                                    // FocusedElement() with nothing in front: "Unexpected HRESULT"
        FlaUiShaped.From(FlaUiShaped.E_FAIL),
        FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE),
        FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT),
        new InvalidOperationException("no automation element for the window handle"),
        new System.Runtime.InteropServices.COMException("raw", unchecked((int)0x80004005)),
    };

    [Theory]
    [MemberData(nameof(CandidateFailures))]
    public void A_candidate_that_throws_falls_through_to_the_next(Exception failure)
    {
        var root = UiRoots.FirstAvailable<Root>(() => throw failure, () => Focused, () => Desktop);

        root.Should().BeSameAs(Focused);
    }

    [Theory]
    [MemberData(nameof(CandidateFailures))]
    public void The_documented_ladder_reaches_the_desktop_when_the_foreground_is_null_and_focus_throws(Exception failure)
    {
        // The exact R24b defect: no foreground window, and FocusedElement() throws.
        var asked = new List<string>();

        var root = UiRoots.FirstAvailable<Root>(
            () => { asked.Add("foreground"); return null; },
            () => { asked.Add("focused"); throw failure; },
            () => { asked.Add("desktop"); return Desktop; });

        root.Should().BeSameAs(Desktop);
        asked.Should().Equal(["foreground", "focused", "desktop"], "each candidate is asked once, in order");
    }

    [Fact]
    public void The_last_candidates_exception_propagates_as_the_same_instance()
    {
        // The last candidate is the unconditional fallback: if even the desktop cannot be had,
        // that failure is the answer, not something swallowed into a null root.
        var fatal = new Win32Exception(5);

        var act = () => UiRoots.FirstAvailable<Root>(() => null, () => throw new Win32Exception(), () => throw fatal);

        act.Should().Throw<Win32Exception>().Which.Should().BeSameAs(fatal);
    }

    [Fact]
    public void A_single_candidate_is_the_unconditional_fallback()
    {
        UiRoots.FirstAvailable<Root>(() => Desktop).Should().BeSameAs(Desktop);

        var fatal = new InvalidOperationException("x");
        var act = () => UiRoots.FirstAvailable<Root>(() => throw fatal);
        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(fatal);
    }

    [Fact]
    public void A_cancellation_in_an_early_candidate_propagates_and_no_later_candidate_is_asked()
    {
        var cancelled = new OperationCanceledException();
        var asked = new List<string>();

        var act = () => UiRoots.FirstAvailable<Root>(
            () => { asked.Add("foreground"); throw cancelled; },
            () => { asked.Add("focused"); return Focused; },
            () => { asked.Add("desktop"); return Desktop; });

        act.Should().Throw<OperationCanceledException>().Which.Should().BeSameAs(cancelled);
        asked.Should().Equal(["foreground"]);
    }

    [Fact]
    public void No_candidates_is_an_argument_error()
    {
        // Ambiguity resolved by test-agent: params allows an empty call; there is no fallback to
        // run, so it is a programming error, refused up front rather than an IndexOutOfRange.
        var act = () => UiRoots.FirstAvailable<Root>();

        act.Should().Throw<ArgumentException>();
    }
}
