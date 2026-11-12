using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-4 (RED) through the REAL service and real UIA: <c>find_element</c>'s automation id, class
/// name and control type filters, <c>within</c> and paging over Win32 controls this process owns
/// (<see cref="OwnedFindFiltersWindow"/>). The fake-tree rows in
/// <see cref="UIAutomationServiceFindFiltersTests"/> prove the rules; these prove the rules hold on
/// what a real provider reports (UIA's Win32 proxy: the control id as AutomationId, the window class
/// as ClassName, a group box as a Group whose child HWND is its UIA child, list rows as ListItems).
/// <para>
/// Read-only: nothing is clicked, typed or focused, and the input mock is STRICT so an accidental
/// pointer or key call fails the row instead of happening. The fixture window is shown without
/// activation at the bottom of the z-order, so every request passes <c>IncludeOffscreen</c> — a
/// covered or scrolled-away row is still the row the test means. Integration, in
/// <see cref="DesktopCollection"/> (a window of ours appears and closes).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public sealed class UIAutomationServiceFindFiltersIntegrationTests : IDisposable
{
    private readonly OwnedFindFiltersWindow _window = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly UIAutomationService _svc;

    public UIAutomationServiceFindFiltersIntegrationTests()
    {
        _svc = new UIAutomationService(_input.Object, new Mock<IWindowService>().Object);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _window.Dispose();
    }

    /// <summary>A request scoped to the fixture window by its (unique) title.</summary>
    private FindElementRequest InWindow(string text = "", string? automationId = null, string? controlType = null,
        string? className = null, int maxResults = 20, int offset = 0)
        => new(text, FindKind.Any, FindScope.Window, _window.Title, IncludeOffscreen: true,
            AutomationId: automationId, ControlType: controlType, ClassName: className,
            MaxResults: maxResults, Offset: offset);

    /// <summary>
    /// The find, retried while it comes back with fewer than <paramref name="atLeast"/> matches:
    /// a desktop enumeration that fails mid-walk (another test's window closing) is skipped by
    /// design (D-5) and reads as an empty result for that one attempt — the precedent is
    /// <c>UIAutomationServiceRefusalIntegrationTests.FindAsync</c>.
    /// </summary>
    private async Task<FindElementResult> FindAsync(FindElementRequest request, int atLeast = 1)
    {
        _window.Exists.Should().BeTrue("precondition: the fixture window was created");
        FindElementResult result = new([]);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            result = await _svc.FindElementAsync(request);
            if (result.Matches.Length >= atLeast) break;
        }
        return result;
    }

    /// <summary>The id of the fixture control named <paramref name="name"/>, through the pre-E-4 find (works today).</summary>
    private async Task<string> IdOfAsync(string name, string controlType)
    {
        _window.Exists.Should().BeTrue("precondition: the fixture window was created");
        ElementInfo? hit = null;
        for (int attempt = 0; attempt < 5 && hit is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            var found = await _svc.FindElementAsync(name, FindKind.Any, FindScope.Window, _window.Title, includeOffscreen: true);
            hit = found.Matches.FirstOrDefault(m => m.Name == name && m.ControlType == controlType);
        }
        hit.Should().NotBeNull($"precondition: UIA sees the fixture's {controlType} '{name}'");
        return hit!.ElementId;
    }

    private static string?[] Aids(FindElementResult r) => r.Matches.Select(m => m.AutomationId).ToArray();

    // ---- harness self-test (green before E-4) ---------------------------------------------------

    /// <summary>
    /// The fixture really has the shape every row below relies on, read with FlaUI directly. Green
    /// before E-4: a red row below is then the service, not the fixture.
    /// </summary>
    [Fact]
    public void Fixture_exposes_the_controls_the_rows_rely_on_through_real_UIA()
    {
        _window.Exists.Should().BeTrue("precondition: the fixture window was created");
        using var automation = new UIA3Automation();
        var cf = automation.ConditionFactory;
        var root = automation.FromHandle(_window.Handle);

        root.Name.Should().Be(_window.Title);

        var unnamed = root.FindFirstDescendant(cf.ByAutomationId(OwnedFindFiltersWindow.UnnamedButtonId));
        unnamed.Should().NotBeNull("the unnamed button is addressable by its control id");
        unnamed.ControlType.Should().Be(ControlType.Button);
        unnamed.Name.Should().BeEmpty("the checklist's 'done when' is an UNNAMED button");
        unnamed.ClassName.Should().Be("Button");

        root.FindAllDescendants(cf.ByName(_window.Twin)).Select(e => e.AutomationId)
            .Should().BeEquivalentTo([OwnedFindFiltersWindow.TwinAId, OwnedFindFiltersWindow.TwinBId]);

        var edit = root.FindFirstDescendant(cf.ByAutomationId(OwnedFindFiltersWindow.EditId))!;
        edit.ControlType.Should().Be(ControlType.Edit);
        edit.ClassName.Should().Be("Edit");

        var group = root.FindFirstDescendant(cf.ByAutomationId(OwnedFindFiltersWindow.GroupId))!;
        group.ControlType.Should().Be(ControlType.Group);
        group.Name.Should().Be(_window.Group);
        group.FindAllDescendants(cf.ByName(_window.Inner)).Select(e => e.AutomationId)
            .Should().Equal([OwnedFindFiltersWindow.InnerInGroupId], "the inner button is the group's UIA child");
        root.FindAllDescendants(cf.ByName(_window.Inner)).Select(e => e.AutomationId)
            .Should().BeEquivalentTo([OwnedFindFiltersWindow.InnerInGroupId, OwnedFindFiltersWindow.InnerOutsideId]);

        var list = root.FindFirstDescendant(cf.ByAutomationId(OwnedFindFiltersWindow.ListId))!;
        list.ControlType.Should().Be(ControlType.List);
        list.FindAllChildren(cf.ByControlType(ControlType.ListItem)).Select(e => e.Name)
            .Should().Equal(Enumerable.Range(0, OwnedFindFiltersWindow.RowCount).Select(_window.Row));

        // The exact sets the filter rows below expect, as UIA itself answers them.
        root.FindAllDescendants(cf.ByClassName("Button")).Select(e => e.AutomationId).Should().BeEquivalentTo(
            [OwnedFindFiltersWindow.UnnamedButtonId, OwnedFindFiltersWindow.TwinAId, OwnedFindFiltersWindow.TwinBId,
             OwnedFindFiltersWindow.GroupId, OwnedFindFiltersWindow.InnerInGroupId, OwnedFindFiltersWindow.InnerOutsideId]);
        root.FindAllDescendants(cf.ByControlType(ControlType.Button).And(cf.ByClassName("Button"))).Select(e => e.AutomationId)
            .Should().BeEquivalentTo(
                [OwnedFindFiltersWindow.UnnamedButtonId, OwnedFindFiltersWindow.TwinAId, OwnedFindFiltersWindow.TwinBId,
                 OwnedFindFiltersWindow.InnerInGroupId, OwnedFindFiltersWindow.InnerOutsideId]);
        root.FindAllDescendants(cf.ByControlType(ControlType.Group)).Select(e => e.AutomationId)
            .Should().Equal(OwnedFindFiltersWindow.GroupId);
        root.FindAllDescendants(cf.ByClassName("Edit")).Select(e => e.AutomationId)
            .Should().Equal(OwnedFindFiltersWindow.EditId);
        root.FindAllDescendants(cf.ByControlType(ControlType.Edit)).Select(e => e.AutomationId)
            .Should().Equal(OwnedFindFiltersWindow.EditId);
    }

    // ---- F1 / F4: automation id -----------------------------------------------------------------

    [Fact]
    public async Task An_unnamed_button_is_found_by_automation_id_inside_a_named_window_as_the_only_match()
    {
        // The checklist's "done when".
        var result = await FindAsync(InWindow(automationId: OwnedFindFiltersWindow.UnnamedButtonId));

        var match = result.Matches.Should().ContainSingle().Subject;
        match.AutomationId.Should().Be(OwnedFindFiltersWindow.UnnamedButtonId);
        match.Name.Should().BeEmpty();
        match.ControlType.Should().Be("Button");
        match.ClassName.Should().Be("Button");
        result.Total.Should().Be(1);
        result.Truncated.Should().BeFalse();
        (await _svc.GetElementAsync(match.ElementId)).AutomationId.Should().Be(OwnedFindFiltersWindow.UnnamedButtonId,
            "the returned id resolves to the same control");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Automation_id_tells_two_buttons_with_the_same_name_apart()
    {
        var result = await FindAsync(InWindow(_window.Twin, automationId: OwnedFindFiltersWindow.TwinBId));

        Aids(result).Should().Equal(OwnedFindFiltersWindow.TwinBId);
        result.Matches[0].Name.Should().Be(_window.Twin);
    }

    // ---- F1 / F2: class name, control type, and both -------------------------------------------

    [Fact]
    public async Task Class_name_matches_the_window_class_case_insensitively()
    {
        var edit = await FindAsync(InWindow(className: "edit"));
        var buttons = await FindAsync(InWindow(className: "BUTTON"), atLeast: 6);

        Aids(edit).Should().Equal(OwnedFindFiltersWindow.EditId);
        Aids(buttons).Should().BeEquivalentTo(
            [OwnedFindFiltersWindow.UnnamedButtonId, OwnedFindFiltersWindow.TwinAId, OwnedFindFiltersWindow.TwinBId,
             OwnedFindFiltersWindow.GroupId, OwnedFindFiltersWindow.InnerInGroupId, OwnedFindFiltersWindow.InnerOutsideId],
            "a group box is window class Button too");
    }

    [Fact]
    public async Task Control_type_matches_the_UIA_type_name()
    {
        var group = await FindAsync(InWindow(controlType: "group"));
        var edit = await FindAsync(InWindow(controlType: "Edit"));

        Aids(group).Should().Equal(OwnedFindFiltersWindow.GroupId);
        Aids(edit).Should().Equal(OwnedFindFiltersWindow.EditId);
    }

    [Fact]
    public async Task Control_type_and_class_name_together_exclude_the_title_bar_buttons_and_the_group_box()
    {
        // ControlType Button alone also matches the title bar's Minimize/Maximize/Close; ClassName
        // Button alone also matches the group box. Together: exactly the five push buttons.
        var result = await FindAsync(InWindow(controlType: "Button", className: "Button"), atLeast: 5);

        Aids(result).Should().BeEquivalentTo(
            [OwnedFindFiltersWindow.UnnamedButtonId, OwnedFindFiltersWindow.TwinAId, OwnedFindFiltersWindow.TwinBId,
             OwnedFindFiltersWindow.InnerInGroupId, OwnedFindFiltersWindow.InnerOutsideId]);
        result.Total.Should().Be(5);
    }

    // ---- W1 / W3: within -------------------------------------------------------------------------

    [Fact]
    public async Task Within_a_group_searches_only_the_groups_descendants()
    {
        // RE-PINNED (R2): was Within_a_group_searches_only_the_group_and_its_subtree, which expected
        // the group itself among the matches.
        var groupId = await IdOfAsync(_window.Group, "Group");

        var inner = await FindAsync(new FindElementRequest(_window.Inner, Within: groupId, IncludeOffscreen: true));
        var all = await FindAsync(new FindElementRequest(Within: groupId, IncludeOffscreen: true));
        var own = await _svc.FindElementAsync(new FindElementRequest(_window.Group, Within: groupId, IncludeOffscreen: true));

        Aids(inner).Should().Equal([OwnedFindFiltersWindow.InnerInGroupId],
            "its namesake outside the group is in the same window but not in the subtree");
        inner.Total.Should().Be(1);
        Aids(all).Should().Contain(OwnedFindFiltersWindow.InnerInGroupId)
            .And.NotContain([OwnedFindFiltersWindow.GroupId, OwnedFindFiltersWindow.InnerOutsideId, OwnedFindFiltersWindow.TwinAId,
                OwnedFindFiltersWindow.EditId, OwnedFindFiltersWindow.ListId]);
        own.Matches.Should().BeEmpty("the group's own name does not find the group: within is its descendants");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Within_an_id_whose_window_was_destroyed_is_Gone()
    {
        var groupId = await IdOfAsync(_window.Group, "Group");
        _window.Close();
        _window.Exists.Should().BeFalse("precondition: the window and its controls are destroyed");

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: groupId, IncludeOffscreen: true));

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>(
            "an empty result would claim the subtree was searched and held nothing")).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Be(UiRefusals.Gone(groupId).Message);
        _input.VerifyNoOtherCalls();
    }

    // ---- P2 / P3 / P4: paging over a long list -------------------------------------------------

    [Fact]
    public async Task Two_consecutive_pages_over_a_long_list_are_consecutive_rows_with_the_real_Total()
    {
        var before = _svc.CachedElementCount;
        var first = await FindAsync(InWindow(controlType: "ListItem", maxResults: 15, offset: 0), atLeast: 15);
        var afterFirst = _svc.CachedElementCount;
        var second = await FindAsync(InWindow(controlType: "ListItem", maxResults: 15, offset: 15), atLeast: 15);
        var last = await FindAsync(InWindow(controlType: "ListItem", maxResults: 15, offset: 30), atLeast: 10);

        first.Matches.Select(m => m.Name).Should().Equal(Enumerable.Range(0, 15).Select(_window.Row));
        second.Matches.Select(m => m.Name).Should().Equal(Enumerable.Range(15, 15).Select(_window.Row),
            "the second page starts where the first ended: no overlap, no gap");
        last.Matches.Select(m => m.Name).Should().Equal(Enumerable.Range(30, 10).Select(_window.Row));
        foreach (var page in new[] { first, second, last })
        {
            page.Total.Should().Be(OwnedFindFiltersWindow.RowCount);
            page.TotalIsLowerBound.Should().BeFalse();
        }
        (first.Offset, second.Offset, last.Offset).Should().Be((0, 15, 30));
        (first.Truncated, second.Truncated, last.Truncated).Should().Be((true, true, false));
        (afterFirst - before).Should().Be(15, "ids are minted for the page only, not for the 25 rows counted past it");
    }

    // ---- P6 / D4: the default request is today's find -------------------------------------------

    [Fact]
    public async Task A_default_request_returns_what_the_positional_find_returns()
    {
        // The token is in the title, the twins, the group, both inner buttons, the list and all 40
        // rows: well past one page, so the cap of 20 is exercised too.
        var request = await FindAsync(InWindow(_window.Token), atLeast: 20);
        FindElementResult positional = new([]);
        for (int attempt = 0; attempt < 5 && positional.Matches.Length < 20; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            positional = await _svc.FindElementAsync(_window.Token, FindKind.Any, FindScope.Window, _window.Title, includeOffscreen: true);
        }

        static IEnumerable<(string, string, Bounds?)> Shape(FindElementResult r) => r.Matches.Select(m => (m.Name, m.ControlType, m.Bounds));
        Shape(request).Should().Equal(Shape(positional), "same elements, same walk order");
        request.Matches.Should().HaveCount(20);
        positional.Total.Should().Be(request.Total, "the positional overload means the same request with defaults");
        positional.Truncated.Should().BeTrue();
        request.Total.Should().BeGreaterThan(20);
    }

    // ---- F5: GetElementAsync carries the two new fields -----------------------------------------

    [Fact]
    public async Task Get_element_reports_the_automation_id_and_class_name()
    {
        var id = await IdOfAsync(_window.Twin, "Button");

        var info = await _svc.GetElementAsync(id);

        info.AutomationId.Should().BeOneOf(OwnedFindFiltersWindow.TwinAId, OwnedFindFiltersWindow.TwinBId);
        info.ClassName.Should().Be("Button");
    }

    // ---- V1: wait_for's element conditions take the filters ------------------------------------

    [Fact]
    public async Task Element_exists_with_an_automation_id_returns_that_control()
    {
        var result = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, _window.Twin, TimeoutMs: 3000, IntervalMs: 100,
            Scope: FindScope.Window, WindowTitle: _window.Title, IncludeOffscreen: true, AutomationId: OwnedFindFiltersWindow.TwinBId));

        result.Satisfied.Should().BeTrue(result.Detail);
        result.Element!.AutomationId.Should().Be(OwnedFindFiltersWindow.TwinBId,
            "twin A comes first in walk order; only the filter picks twin B");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Element_exists_with_empty_text_and_an_automation_id_returns_the_unnamed_button()
    {
        // Decision 5, live: the unnamed control has no text to wait for; its automation id names it.
        var result = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "", TimeoutMs: 3000, IntervalMs: 100,
            Scope: FindScope.Window, WindowTitle: _window.Title, IncludeOffscreen: true, AutomationId: OwnedFindFiltersWindow.UnnamedButtonId));

        result.Satisfied.Should().BeTrue(result.Detail);
        result.Element!.AutomationId.Should().Be(OwnedFindFiltersWindow.UnnamedButtonId);
        result.Element.Name.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wait_within_a_container_whose_window_closed_is_refused_with_Gone()
    {
        // RE-PINNED (R5): was A_wait_within_a_container_whose_window_closed_is_not_refused_and_every_poll_fails
        // (decision 4). A container already dead when the wait starts is refused up front, with
        // the answer find_element gives.
        var groupId = await IdOfAsync(_window.Group, "Group");
        _window.Close();
        _window.Exists.Should().BeFalse("precondition: the window and its controls are destroyed");

        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, _window.Inner, TimeoutMs: 300, IntervalMs: 100,
            IncludeOffscreen: true, Within: groupId));

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Be(UiRefusals.Gone(groupId).Message);
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Element_exists_within_a_group_finds_only_the_button_inside_it()
    {
        var groupId = await IdOfAsync(_window.Group, "Group");

        var inside = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, _window.Inner, TimeoutMs: 3000, IntervalMs: 100,
            IncludeOffscreen: true, Within: groupId));
        var twin = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, _window.Twin, TimeoutMs: 300, IntervalMs: 100,
            IncludeOffscreen: true, Within: groupId));

        inside.Satisfied.Should().BeTrue(inside.Detail);
        inside.Element!.AutomationId.Should().Be(OwnedFindFiltersWindow.InnerInGroupId);
        twin.Satisfied.Should().BeFalse("the twins are in the window, not in the group");
        twin.Detail.Should().Be($"no element matching '{_window.Twin}'");
        _input.VerifyNoOtherCalls();
    }
}
