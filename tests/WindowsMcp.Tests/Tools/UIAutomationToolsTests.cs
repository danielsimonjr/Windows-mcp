using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tools;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class UIAutomationToolsTests
{
    /// <summary>
    /// E-4: <c>find_element</c> reaches the service through ONE of the two
    /// <c>FindElementAsync</c> overloads — the positional one before E-4, the
    /// <see cref="FindElementRequest"/> one after it (pinned by
    /// <c>UIAutomationToolsFindFiltersTests</c>). These pre-E-4 rows pin WHAT is forwarded, not
    /// which overload carries it: both are recorded here as the request they mean (the positional
    /// overload is "the same request with defaults", E-4 D4), and the row asserts exactly one call.
    /// </summary>
    private static (Mock<IUIAutomationService> Mock, List<FindElementRequest> Seen) Finding(FindElementResult result)
    {
        var seen = new List<FindElementRequest>();
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.FindElementAsync(It.IsAny<string>(), It.IsAny<FindKind>(), It.IsAny<FindScope>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((string text, FindKind kind, FindScope scope, string? window, bool offscreen, CancellationToken _) =>
                seen.Add(new FindElementRequest(text, kind, scope, window, offscreen)))
            .ReturnsAsync(result);
        mock.Setup(s => s.FindElementAsync(It.IsAny<FindElementRequest>(), It.IsAny<CancellationToken>()))
            .Callback((FindElementRequest r, CancellationToken _) => seen.Add(r))
            .ReturnsAsync(result);
        return (mock, seen);
    }

    [Fact]
    public async Task FindElement_passes_interactive_kind_to_service()
    {
        var element = new ElementInfo("el-1", "Submit", "Button", true, false,
            new Bounds(10, 20, 100, 30), null, null, null);
        var (mock, seen) = Finding(new FindElementResult(new[] { element }));
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.FindElement("Submit", "interactive");

        result.Should().Contain("Submit").And.Contain("el-1");
        seen.Should().ContainSingle().Which.Should().Be(new FindElementRequest("Submit", FindKind.Interactive, FindScope.Foreground));
    }

    /// <summary>
    /// Not A-5, but the one line of <c>UIAutomationTools</c> the A-5 coverage sweep found
    /// unreached: <c>"scrollable" =&gt; FindKind.Scrollable</c>. The description advertises
    /// <c>any|interactive|text|scrollable</c>, so all four are requirements.
    /// </summary>
    [Theory]
    [InlineData("any", FindKind.Any)]
    [InlineData("interactive", FindKind.Interactive)]
    [InlineData("text", FindKind.Text)]
    [InlineData("scrollable", FindKind.Scrollable)]
    [InlineData("SCROLLABLE", FindKind.Scrollable)]
    public async Task FindElement_maps_every_advertised_kind(string kind, FindKind expected)
    {
        var (mock, seen) = Finding(new FindElementResult([]));

        await new UIAutomationTools(mock.Object).FindElement("Save", kind);

        seen.Should().ContainSingle().Which.Should().Be(new FindElementRequest("Save", expected, FindScope.Foreground));
    }

    [Fact]
    public async Task FindElement_rejects_unknown_kind_with_clear_message()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", "unknown_kind");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*kind*");
    }

    // D-2: the tool forwards the action and value untouched and reports what actually fired.
    [Fact]
    public async Task InteractElement_forwards_arguments_and_reports_what_fired()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.InteractAsync("el-1", "type", "hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractResult("el-1", "type", "Keyboard", "typed at the caret"));
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.InteractElement("el-1", "type", "hi");

        result.Should().Contain("\"Method\":\"Keyboard\"").And.Contain("el-1");
        mock.VerifyAll();
    }

    // D-4: the tool forwards `expected` untouched and renders the observed state on FAIL.
    [Fact]
    public async Task AssertElement_forwards_expected_and_renders_the_observed_state()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.AssertElementAsync("el-1", "value", "hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssertResult("el-1", "value", false, "value is 'ho' (from ValuePattern)"));
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.AssertElement("el-1", "value", "hi");

        result.Should().Be("FAIL: value — observed value is 'ho' (from ValuePattern)");
        mock.VerifyAll();
    }

    [Fact]
    public async Task AssertElement_renders_a_bare_PASS()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.AssertElementAsync("el-1", "enabled", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssertResult("el-1", "enabled", true, "enabled"));
        var tools = new UIAutomationTools(mock.Object);

        (await tools.AssertElement("el-1", "enabled")).Should().Be("PASS");
        mock.VerifyAll();
    }

    // ---- D-5 / D-7: scope, window target, and the off-screen flag reach the service ------------

    [Fact]
    public async Task FindElement_defaults_to_the_foreground_window_and_drops_offscreen()
    {
        var (mock, seen) = Finding(new FindElementResult(Array.Empty<ElementInfo>()));
        var tools = new UIAutomationTools(mock.Object);

        await tools.FindElement("Save");

        seen.Should().ContainSingle().Which.Should().Be(new FindElementRequest("Save", FindKind.Any, FindScope.Foreground, null, false));
    }

    [Fact]
    public async Task FindElement_forwards_window_scope_with_its_title()
    {
        var (mock, seen) = Finding(new FindElementResult(Array.Empty<ElementInfo>()));
        var tools = new UIAutomationTools(mock.Object);

        await tools.FindElement("Save", scope: "window", window: "Notepad");

        seen.Should().ContainSingle().Which.Should().Be(new FindElementRequest("Save", FindKind.Any, FindScope.Window, "Notepad", false));
    }

    [Fact]
    public async Task FindElement_forwards_desktop_scope_and_include_offscreen()
    {
        var (mock, seen) = Finding(new FindElementResult(Array.Empty<ElementInfo>()));
        var tools = new UIAutomationTools(mock.Object);

        await tools.FindElement("", "text", scope: "desktop", include_offscreen: true);

        seen.Should().ContainSingle().Which.Should().Be(new FindElementRequest("", FindKind.Text, FindScope.Desktop, null, true));
    }

    [Fact]
    public async Task FindElement_rejects_unknown_scope_with_clear_message()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", scope: "everywhere");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*everywhere*");
    }

    // A window title only means something with scope=window. Rejecting the mismatch rather than
    // ignoring the argument is the D-4 `expected` rule.
    [Fact]
    public async Task FindElement_rejects_window_scope_without_a_title()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", scope: "window");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*requires window*");
    }

    [Fact]
    public async Task FindElement_rejects_a_window_title_with_another_scope()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", scope: "desktop", window: "Notepad");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
    }

    // B-6 (roadmap C4) moved the wait_for tool rows to UIAutomationToolsWaitForTests: the tool now
    // takes a condition, calls the WaitRequest overload, and ALWAYS returns a WaitForResult. The
    // two rows that used to live here pinned the pre-B-6 contract - forwarding to
    // WaitForAsync(text, ...) and rendering the string "null" on timeout - which is exactly what
    // C4 replaces. The old service overload itself is unchanged and still pinned, by
    // UIAutomationServiceTests (PollAsync) and WaitForFindPathIntegrationTests.

    // ---- A-2 (R5): the snapshot tool ---------------------------------------------------------
    // The tool is the whole contract the model sees: what it may pass, what comes back, and what
    // an id is good for. Every argument rule is decided HERE, before the service is touched, and
    // the two output formats are exactly "the renderer" and "the DTOs" - no third shape.

    /// <summary>A service that answers every snapshot with <paramref name="result"/>.</summary>
    private static Mock<IUIAutomationService> SnapshotService(SnapshotResult? result = null)
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? Result());
        return mock;
    }

    /// <summary>A snapshot with one of everything, so a dropped block shows up in the output.</summary>
    private static SnapshotResult PopulatedResult(ElementTree? tree = null) => Result(
        windows: [Window()],
        active: Window(isActive: true),
        interactive: [Element(shortcut: "Ctrl+S")],
        scrollable: [Scrollable()],
        tree: tree,
        elementCount: 57);

    [Theory]
    [InlineData("desktop", null, SnapshotScope.Desktop)]
    [InlineData("foreground", null, SnapshotScope.Foreground)]
    [InlineData("window", "Notepad", SnapshotScope.Window)]
    [InlineData("DESKTOP", null, SnapshotScope.Desktop)]      // scope is case-insensitive, as in find_element
    public async Task Snapshot_maps_the_scope_and_calls_the_service_once(string scope, string? window, SnapshotScope expected)
    {
        var mock = SnapshotService();
        var tools = new UIAutomationTools(mock.Object);

        await tools.Snapshot(scope, window);

        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == expected
                                     && r.WindowTitle == window
                                     && !r.IncludeTree
                                     && r.MaxElements == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Snapshot_forwards_include_tree_and_max_elements()
    {
        var mock = SnapshotService();
        var tools = new UIAutomationTools(mock.Object);

        await tools.Snapshot("foreground", include_tree: true, max_elements: 25);

        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == SnapshotScope.Foreground && r.IncludeTree && r.MaxElements == 25),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Snapshot_rejects_an_unknown_scope_naming_the_three_choices()
    {
        var mock = SnapshotService();
        var tools = new UIAutomationTools(mock.Object);

        Func<Task> act = () => tools.Snapshot("everywhere");

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("everywhere");
        foreach (var choice in new[] { "desktop", "foreground", "window" })
            message.Should().Contain(choice);
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Snapshot_rejects_window_scope_without_a_window()
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot("window");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*requires window*");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("foreground")]
    public async Task Snapshot_rejects_a_window_with_another_scope(string scope)
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot(scope, "Notepad");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Snapshot_rejects_a_negative_max_elements(int max)
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot(max_elements: max);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("max_elements", "the message names the argument the caller got wrong");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Snapshot_passes_max_elements_zero_through_as_the_server_default()
    {
        // 0 is not "no elements": the service turns it into the --max-tree-elements budget.
        var mock = SnapshotService();

        await new UIAutomationTools(mock.Object).Snapshot(max_elements: 0);

        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.MaxElements == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("TEXT")]
    [InlineData("Json")]
    public async Task Snapshot_accepts_both_formats_case_insensitively(string format)
    {
        var tools = new UIAutomationTools(SnapshotService(PopulatedResult()).Object);

        var output = await tools.Snapshot(format: format);

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            output.Should().StartWith("{", "the json form is the serialised SnapshotResult");
        else
            output.Should().StartWith("Cursor:", "the text form starts with the renderer's header");
    }

    // ---- A-14 (R4): a profiled snapshot's timings reach the caller in BOTH formats -----------
    // SnapshotRendererTests pins the text line and StageTimingDtosTests the JSON shape; both call
    // their target directly. This is the tool: it must not strip, re-order or re-serialise the
    // stages on the way out - json is the DTO, text is the renderer, and there is no third shape.

    [Fact]
    public async Task Snapshot_json_carries_the_stage_timings_the_service_reported()
    {
        var profiled = PopulatedResult() with { CaptureMs = 142, Stages = [new("header", 12), new("walk", 130)] };
        var tools = new UIAutomationTools(SnapshotService(profiled).Object);

        var output = await tools.Snapshot(format: "json");

        using var doc = JsonDocument.Parse(output);
        var stages = doc.RootElement.GetProperty("Stages");
        stages.GetArrayLength().Should().Be(2);
        stages[0].GetProperty("Stage").GetString().Should().Be("header");
        stages[1].GetProperty("Ms").GetInt64().Should().Be(130);
        doc.RootElement.GetProperty("CaptureMs").GetInt64().Should().Be(142);
    }

    [Fact]
    public async Task Snapshot_text_ends_with_the_timing_line_when_the_snapshot_was_profiled()
    {
        var profiled = PopulatedResult() with { CaptureMs = 142, Stages = [new("header", 12), new("walk", 130)] };
        var tools = new UIAutomationTools(SnapshotService(profiled).Object);

        var output = await tools.Snapshot(format: "text");

        output.Split('\n').Last().Should().Be("Timing: header 12 ms, walk 130 ms (total 142 ms)");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public async Task Snapshot_says_nothing_about_timings_when_profiling_is_off(string format)
    {
        // The default server: neither output form mentions a stage, so no existing caller's
        // parsing changes because A-14 shipped.
        var tools = new UIAutomationTools(SnapshotService(PopulatedResult()).Object);

        var output = await tools.Snapshot(format: format);

        output.Should().NotContain("Stages").And.NotContain("Timing:");
    }

    [Fact]
    public async Task Snapshot_rejects_an_unknown_format_naming_both_choices()
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot(format: "yaml");

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("yaml").And.Contain("text").And.Contain("json");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- A-5 phase 1 (R6): use_dom is forwarded, not refused ---------------------------------
    // Before A-5 the tool threw on use_dom:true. It is now a request flag like include_tree: the
    // tool decides nothing about the DOM, it only passes the caller's intent to the service.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Snapshot_forwards_use_dom_to_the_service(bool useDom)
    {
        var mock = SnapshotService();

        await new UIAutomationTools(mock.Object).Snapshot(use_dom: useDom);

        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.UseDom == useDom), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Snapshot_does_not_use_the_dom_unless_it_was_asked_to()
    {
        // The default call must reach the service with UseDom false - browser DOM mode changes
        // WHAT is walked, so a silent default-on would change every existing snapshot.
        var mock = SnapshotService();

        await new UIAutomationTools(mock.Object).Snapshot();

        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => !r.UseDom), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Snapshot_use_dom_no_longer_refuses()
    {
        var mock = SnapshotService(PopulatedResult() with { Pages = [Page()] });

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot(use_dom: true);

        await act.Should().NotThrowAsync("A-5 phase 1 implements browser DOM mode for Chromium");
    }

    [Fact]
    public void Snapshot_use_dom_is_still_the_last_parameter_and_defaults_to_false()
    {
        // The MCP schema is positional-by-name, but the parameter ORDER is what every existing
        // caller and the HTTP schema test rely on; A-5 changes the behaviour, not the signature.
        var parameters = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.Snapshot))!.GetParameters();

        parameters[^1].Name.Should().Be("use_dom");
        parameters[^1].ParameterType.Should().Be(typeof(bool));
        parameters[^1].HasDefaultValue.Should().BeTrue();
        parameters[^1].DefaultValue.Should().Be(false);
    }

    [Fact]
    public async Task Snapshot_json_carries_the_pages_the_service_reported()
    {
        var withPages = PopulatedResult() with
        {
            Pages = [Page(text: ["Probe heading", "First paragraph of body text."])],
        };
        var tools = new UIAutomationTools(SnapshotService(withPages).Object);

        var output = await tools.Snapshot(use_dom: true, format: "json");

        using var doc = JsonDocument.Parse(output);
        var page = doc.RootElement.GetProperty("Pages")[0];
        page.GetProperty("DocumentId").GetString().Should().Be("el_7");
        page.GetProperty("Title").GetString().Should().Be("A5 Probe Page");
        page.GetProperty("Url").GetString().Should().Be("http://127.0.0.1:9999/a5");
        page.GetProperty("Text")[0].GetString().Should().Be("Probe heading");
    }

    [Fact]
    public async Task Snapshot_text_carries_the_rendered_pages_block()
    {
        var withPages = PopulatedResult() with { Pages = [Page(text: ["Probe heading"])] };
        var tools = new UIAutomationTools(SnapshotService(withPages).Object);

        var output = await tools.Snapshot(use_dom: true);

        output.Should().Contain("Pages (1):")
              .And.Contain("el_7 \"A5 Probe Page\" http://127.0.0.1:9999/a5")
              .And.Contain("\n    Probe heading");
    }

    [Fact]
    public async Task Snapshot_json_carries_an_empty_pages_array_when_dom_mode_found_no_browser()
    {
        // "Pages": [] is an answer the model can act on ("no browser is open"); a missing key
        // would be indistinguishable from "the flag was dropped on the way to the service".
        var tools = new UIAutomationTools(SnapshotService(PopulatedResult() with { Pages = [] }).Object);

        var output = await tools.Snapshot(use_dom: true, format: "json");

        using var doc = JsonDocument.Parse(output);
        var pages = doc.RootElement.GetProperty("Pages");
        pages.ValueKind.Should().Be(JsonValueKind.Array);
        pages.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_text_carries_an_empty_pages_block_when_dom_mode_found_no_browser()
    {
        var tools = new UIAutomationTools(SnapshotService(PopulatedResult() with { Pages = [] }).Object);

        var output = await tools.Snapshot(use_dom: true);

        output.Should().Contain("Pages (0):");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public async Task Snapshot_says_nothing_about_pages_when_the_dom_was_not_asked_for(string format)
    {
        // PopulatedResult carries Pages: null - what the service returns for use_dom:false - so
        // neither output form changes for a caller who never asked (the A-14 rule).
        var tools = new UIAutomationTools(SnapshotService(PopulatedResult()).Object);

        var output = await tools.Snapshot(format: format);

        output.Should().NotContain("Pages");
    }

    [Fact]
    public async Task Snapshot_text_is_exactly_the_rendered_layout()
    {
        var result = PopulatedResult();
        var tools = new UIAutomationTools(SnapshotService(result).Object);

        var output = await tools.Snapshot();

        output.Should().Be(SnapshotRenderer.Render(result),
            "text is the default format and the renderer is the one place the layout lives");
    }

    [Fact]
    public async Task Snapshot_json_is_the_serialised_result()
    {
        var result = PopulatedResult();
        var tools = new UIAutomationTools(SnapshotService(result).Object);

        var output = await tools.Snapshot(format: "json");

        output.Should().Be(JsonSerializer.Serialize(result));
        output.Should().Contain("\"ElementId\":\"el_12\"").And.Contain("\"CenterX\":612");
        output.Should().Contain("\"Interactive\"").And.Contain("\"Scrollable\"").And.Contain("\"ElementCount\":57");
    }

    [Fact]
    public async Task Snapshot_json_carries_the_tree_only_when_it_was_asked_for()
    {
        var treeRoot = new ElementInfo("desktop", "", "Desktop", true, false, null, null, null, null);
        var withTree = PopulatedResult(new ElementTree(treeRoot, []));
        var withoutTree = PopulatedResult();

        var asked = await new UIAutomationTools(SnapshotService(withTree).Object)
            .Snapshot(include_tree: true, format: "json");
        var notAsked = await new UIAutomationTools(SnapshotService(withoutTree).Object)
            .Snapshot(format: "json");

        using var askedDoc = JsonDocument.Parse(asked);
        askedDoc.RootElement.GetProperty("Tree").GetProperty("Root").GetProperty("ElementId")
            .GetString().Should().Be("desktop");

        using var notAskedDoc = JsonDocument.Parse(notAsked);
        notAskedDoc.RootElement.GetProperty("Tree").ValueKind.Should().Be(JsonValueKind.Null,
            "the tree is the expensive block; it is absent unless include_tree asked for it");
    }

    // The description is the only spec the model reads before it calls the tool: what one call
    // returns, how long an id lives, and which tools accept it.
    [Fact]
    public void Snapshot_description_tells_the_model_what_it_gets_and_how_long_the_ids_live()
    {
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.Snapshot))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (var fragment in new[]
                 {
                     "one call", "desktop",                        // what it is
                     "text", "json",                               // the formats, text being default
                     "scope", "window", "max_elements",            // the arguments that change the walk
                     "--max-tree-elements",                        // where the 0 default comes from
                     "next snapshot",                              // roadmap C5: id lifetime
                     "click", "interact_element", "get_element",   // what an id is good for
                     "use_dom",                                    // A-5: browser DOM mode
                 })
            description.Should().ContainEquivalentOf(fragment);

        description.Should().MatchRegex("cent(re|er)", "the model is told the coordinates are the element's centre");
        description.Should().MatchRegex("[Tt]runcat", "the truncation note is advertised, not a surprise");
    }

    /// <summary>
    /// A-5 phase 1 (R6): the description is the only place the model learns what <c>use_dom</c>
    /// now DOES. Before A-5 it said "reserved … not implemented yet"; a description that still
    /// says that is a working feature the model will never call.
    /// </summary>
    [Fact]
    public void Snapshot_description_explains_what_use_dom_does_and_no_longer_calls_it_reserved()
    {
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.Snapshot))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (var fragment in new[]
                 {
                     "use_dom",
                     "RootWebArea",      // the document the walk starts from
                     "Chromium",         // and the one engine phase 1 supports
                     "Pages",            // the block the caller gets back
                     "Firefox",          // the documented follow-up: walked whole, with a note
                 })
            description.Should().ContainEquivalentOf(fragment);

        description.Should().NotContainEquivalentOf("not implemented");
        description.Should().NotContainEquivalentOf("reserved");
    }

    [Fact]
    public void Snapshot_use_dom_parameter_description_tells_the_model_what_it_gets()
    {
        // The per-parameter description is what the JSON schema carries; a model that reads only
        // the schema must still learn that this walks the page and adds the Pages block.
        var parameter = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.Snapshot))!
            .GetParameters().Single(p => p.Name == "use_dom");
        var description = parameter.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().ContainEquivalentOf("page");
        description.Should().NotContainEquivalentOf("not implemented");
        description.Should().NotContainEquivalentOf("reserved");
    }

    // ---- E-2: interact_element's expand, collapse, set_value, scroll_into_view ------------------

    private static readonly string[] InteractVerbs =
        ["click", "invoke", "toggle", "select", "focus", "type", "expand", "collapse", "set_value", "scroll_into_view"];

    private static string InteractDescription() => typeof(UIAutomationTools)
        .GetMethod(nameof(UIAutomationTools.InteractElement))!
        .GetCustomAttribute<DescriptionAttribute>()!.Description;

    private static string InteractParameterDescription(string name) => typeof(UIAutomationTools)
        .GetMethod(nameof(UIAutomationTools.InteractElement))!
        .GetParameters().Single(p => p.Name == name)
        .GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Fact]
    public void InteractElement_action_parameter_advertises_the_ten_verbs_in_order()
    {
        InteractParameterDescription("action")
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal(InteractVerbs);
    }

    [Fact]
    public void InteractElement_description_names_every_verb()
    {
        var description = InteractDescription();

        foreach (var verb in InteractVerbs)
            description.Should().Contain(verb);
    }

    [Fact]
    public void InteractElement_value_parameter_description_says_set_value_takes_a_number_or_text()
    {
        var description = InteractParameterDescription("value");

        description.Should().Contain("set_value");
        description.Should().ContainEquivalentOf("number", "a range control (slider) takes a number");
        description.Should().ContainEquivalentOf("text", "a ValuePattern control takes the text");
    }

    [Fact]
    public void InteractElement_description_says_what_the_new_refusals_tell_the_caller()
    {
        var description = InteractDescription();

        description.Should().ContainEquivalentOf("minimum").And.ContainEquivalentOf("maximum",
            "set_value's out-of-range refusal names the range");
        description.Should().ContainEquivalentOf("scrollable",
            "scroll_into_view's refusal points at the scrollable container to scroll instead");
    }

    // ---- E-2 round 2 (I, J): the description says what the real providers do ------------------

    [Fact]
    public void InteractElement_description_no_longer_promises_no_focus_change()
    {
        // I: UIA's Win32 providers activate the control's window on ValuePattern/RangeValuePattern
        // SetValue (UIAutomationServiceInteractVerbsRealPatternTests' class note), so the promise is false.
        InteractDescription().Should().NotContainEquivalentOf("no focus change");
    }

    [Fact]
    public void InteractElement_description_says_set_value_makes_no_focus_call_has_no_typing_fallback_and_a_provider_may_bring_the_window_forward()
    {
        var description = InteractDescription();

        description.Should().ContainEquivalentOf("no focus call");
        description.Should().ContainEquivalentOf("no typing fallback");
        description.Should().MatchRegex("(?i)provider[^.;]*(window[^.;]*(forward|to the foreground|to the front))",
            "a provider may bring the control's window forward");
    }

    [Fact]
    public void InteractElement_description_says_set_value_reports_the_value_shown_and_refuses_when_it_did_not_change()
    {
        var description = InteractDescription();

        description.Should().MatchRegex("(?i)set_value[^)]*(did not change|does not change|didn't change|unchanged)",
            "J: set_value refuses when the control's value did not change");
        description.Should().MatchRegex("(?i)set_value[^)]*(shows|reads back|value afterwards|value after)",
            "J: set_value reports the value the control shows afterwards");
    }

    [Fact]
    public void InteractElement_description_says_the_range_in_a_refusal_is_the_providers_and_a_Win32_trackbar_reports_percent()
    {
        var description = InteractDescription();

        description.Should().MatchRegex("(?i)provider'?s", "J: the minimum and maximum named are the provider's");
        description.Should().MatchRegex("(?i)trackbar[^.;)]*(0-100|0 to 100|0–100)[^.;)]*percent|percent[^.;)]*trackbar",
            "J: a Win32 trackbar reports 0-100 percent");
    }

    [Fact]
    public void InteractElement_description_says_scroll_into_view_on_an_element_already_on_screen_succeeds()
    {
        InteractDescription().Should().ContainEquivalentOf("already on screen");
    }

    [Theory]
    [InlineData("expand", null, "ExpandCollapsePattern", "now Expanded")]
    [InlineData("set_value", "40", "RangeValuePattern", "now 40 (range 0 to 100)")]
    [InlineData("scroll_into_view", null, "ScrollItemPattern", null)]
    public async Task InteractElement_forwards_a_new_verb_and_returns_the_unchanged_result_shape(
        string action, string? value, string method, string? detail)
    {
        // Regression guard (green before E-2): the tool is a pass-through, and the JSON keeps the
        // {ElementId, Action, Method, Detail} shape for the new verbs.
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.InteractAsync("el_3", action, value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractResult("el_3", action, method, detail));

        var json = await new UIAutomationTools(mock.Object).InteractElement("el_3", action, value);

        mock.Verify(s => s.InteractAsync("el_3", action, value, It.IsAny<CancellationToken>()), Times.Once);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("ElementId", "Action", "Method", "Detail");
        root.GetProperty("ElementId").GetString().Should().Be("el_3");
        root.GetProperty("Action").GetString().Should().Be(action);
        root.GetProperty("Method").GetString().Should().Be(method);
        root.GetProperty("Detail").GetString().Should().Be(detail);
    }
}
