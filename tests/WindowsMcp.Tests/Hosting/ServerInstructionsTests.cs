using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Hosting;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Services.UiTree;
using WindowsMcp.Tests.Tools;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// E-1: the text <see cref="ServerInstructions.Text"/> that every client receives in the
/// <c>initialize</c> result, whatever the transport. These are the properties of the text itself;
/// <c>HttpTransportTests</c> and <c>StreamTransportTests</c> prove it actually crosses the wire.
/// <para>
/// The instructions are the only place a client that never installed the plugin skill learns the
/// snapshot -> act -> confirm loop, so they are held to the same standard as a tool description:
/// every tool they name must exist (a rename cannot leave them stale), the loop must really be
/// stated, and they must fit in what a client keeps.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ServerInstructionsTests
{
    private static string Text => ServerInstructions.Text;

    private static string[] Lines => Text.Split('\n');

    /// <summary>
    /// Exactly what <c>WithToolsFromAssembly</c> discovers (the same reflection
    /// <c>ToolInventoryTests</c> uses): public methods carrying <c>[McpServerTool]</c> on a type
    /// carrying <c>[McpServerToolType]</c>.
    /// </summary>
    private static MethodInfo[] ToolMethods() =>
        typeof(InputTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

    /// <summary>
    /// The wire names of the discovered tools, as the SDK itself derives them (the attribute's
    /// <c>Name</c>, else the SDK's snake_case of the method name) - not a hand-rolled conversion
    /// that could disagree with the SDK, and not a hard-coded list that a rename would leave stale.
    /// The target factory is never invoked: only the tool's metadata is read.
    /// </summary>
    private static HashSet<string> WireToolNames() =>
        ProtocolTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>The discovered tools' protocol metadata (name and input schema), as the SDK builds it.</summary>
    private static Tool[] ProtocolTools() =>
        ToolMethods()
            .Select(m => m.IsStatic
                ? McpServerTool.Create(m, (object?)null)
                : McpServerTool.Create(m, (RequestContext<CallToolRequestParams> _) =>
                    throw new InvalidOperationException("metadata only - the tool is never invoked")))
            .Select(t => t.ProtocolTool)
            .ToArray();

    /// <summary>
    /// The backtick-quoted tokens of <paramref name="text"/> that are bare lowercase identifiers
    /// (<c>^[a-z][a-z0-9_]*$</c>). By the text's own convention these are tool names and nothing
    /// else: a parameter or field is written <c>`confirm: true`</c> or <c>`el_N`</c>, which this
    /// does not take.
    /// </summary>
    internal static string[] BacktickedIdentifiers(string text) =>
        Regex.Matches(text, "`([^`\n]+)`")
            .Select(m => m.Groups[1].Value)
            .Where(token => Regex.IsMatch(token, "^[a-z][a-z0-9_]*$"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    // ---- R1 (unit half): there is something to send --------------------------------------------

    [Fact]
    public void Text_is_not_empty()
    {
        Text.Should().NotBeNullOrWhiteSpace(
            "E-1: a client that has never seen the skill must be told the loop at connect time");
    }

    // ---- R3: tool names cannot go stale --------------------------------------------------------

    /// <summary>
    /// Guards the discovery the next test checks against: if the SDK naming or the reflection
    /// broke, every token would be "unknown" (or, worse, the set could be empty and the sweep
    /// vacuous). Pinned with names whose snake_case is non-trivial.
    /// </summary>
    [Fact]
    public void Wire_tool_names_are_the_sdk_names_of_the_discovered_set()
    {
        var names = WireToolNames();

        names.Should().HaveCount(ToolMethods().Length, "one wire name per discovered tool method, no collisions");
        names.Should().Contain(["snapshot", "interact_element", "assert_element", "wait_for", "wait",
            "screenshot", "file_write", "get_state"]);
    }

    /// <summary>
    /// The extraction itself: it takes a bare tool name and leaves parameters, ids, field names
    /// and calls with arguments alone. Green against the stub by design - it tests the checker,
    /// not the text.
    /// </summary>
    [Theory]
    [InlineData("call `snapshot` first", new[] { "snapshot" })]
    [InlineData("then `interact_element` or `wait_for`", new[] { "interact_element", "wait_for" })]
    [InlineData("pass `confirm: true`", new string[0])]
    [InlineData("ids look like `el_N`", new string[0])]
    [InlineData("read `Truncated`", new string[0])]
    [InlineData("`window(action:\"list\")` lists them", new string[0])]
    [InlineData("an empty pair `` is nothing", new string[0])]
    public void Backtick_extraction_takes_only_bare_lowercase_identifiers(string text, string[] expected)
    {
        BacktickedIdentifiers(text).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Every_backticked_tool_name_is_a_discovered_tool()
    {
        var named = BacktickedIdentifiers(Text);
        var tools = WireToolNames();

        named.Should().NotBeEmpty("the instructions name the tools of the loop, so the sweep below is not vacuous");
        named.Where(n => !tools.Contains(n)).Should().BeEmpty(
            "every backticked bare identifier in the instructions is a tool name, and a tool that was "
            + "renamed or removed would send every client a name that does not exist");
    }

    // ---- R4: the loop is actually stated -------------------------------------------------------

    [Theory]
    [InlineData("snapshot")]
    [InlineData("interact_element")]
    [InlineData("assert_element")]
    [InlineData("wait_for")]
    [InlineData("wait")]
    [InlineData("screenshot")]
    public void Text_names_the_loop_tool(string tool)
    {
        Text.Should().Contain($"`{tool}`",
            "the snapshot -> act -> confirm loop is stated with the tools that run it, backticked so "
            + "the staleness sweep checks them");
    }

    [Theory]
    [InlineData("el_N")]        // element ids are what the act step is given
    [InlineData("Truncated")]   // a capped walk says so, and the model must read it
    [InlineData("confirm")]     // the gated changes are refused without confirm: true
    public void Text_states_the_loop_term(string term)
    {
        Text.Should().Contain(term);
    }

    /// <summary>
    /// The instructions tell the model to stop when a result carries a takeover note, so they must
    /// use the words the note really uses - otherwise the model is told to watch for a phrase that
    /// never appears.
    /// </summary>
    [Fact]
    public void The_takeover_phrase_is_the_one_the_takeover_note_uses()
    {
        var now = DateTimeOffset.UtcNow;
        var note = WindowsMcpHost.TakeoverNote(new TakeoverRecord(now, "Ctrl+Alt+Shift+Backspace"), now);

        note.Should().Contain("took over", "the phrase pinned below is the note's own wording");
        Text.Should().Contain("took over",
            "the instructions name the takeover note by the words it actually says");
    }

    /// <summary>
    /// E-1 GREEN: the instructions do not just say "took over" - they QUOTE the note, eliding the
    /// variable parts with "...". Every quoted fragment must be in the real note, in order, or the
    /// model is told to watch for a sentence the server never writes.
    /// </summary>
    [Fact]
    public void The_quoted_takeover_note_matches_the_real_note_fragment_by_fragment()
    {
        var quoted = Regex.Match(Text, "\"(Note: [^\"]*)\"");
        quoted.Success.Should().BeTrue("the instructions quote the takeover note so the model can recognise it");
        var now = DateTimeOffset.UtcNow;
        var note = WindowsMcpHost.TakeoverNote(new TakeoverRecord(now, "Ctrl+Alt+Shift+Backspace"), now);

        var fragments = quoted.Groups[1].Value.Split("...").Select(f => f.Trim()).Where(f => f.Length > 0).ToArray();
        fragments.Should().HaveCountGreaterThan(1, "the quote elides the hotkey and the time");
        note.Should().StartWith(fragments[0], "the quote starts where the note starts");
        var at = 0;
        foreach (var fragment in fragments)
        {
            var found = note.IndexOf(fragment, at, StringComparison.Ordinal);
            found.Should().BeGreaterThanOrEqualTo(0, "'{0}' is quoted from the note, which reads: {1}", fragment, note);
            at = found + fragment.Length;
        }
    }

    // ---- R8 (GREEN): parameters and result fields the text names are real ---------------------

    /// <summary>
    /// The staleness argument of R3 applies to parameters just as much: the text tells the model
    /// to pass these arguments to these tools (unbackticked, by the text's own convention, so the
    /// R3 sweep does not see them). A renamed parameter would leave every client calling a tool
    /// with an argument it does not take. When the text writes the argument with <c>:true</c>, the
    /// parameter must be a boolean too.
    /// </summary>
    [Theory]
    [InlineData("snapshot", "scope", "narrow it with scope")]
    [InlineData("snapshot", "window", "scope and window")]
    [InlineData("snapshot", "use_dom", "use_dom:true")]
    [InlineData("interact_element", "element_id", "`interact_element` acts on an el_N id")]
    [InlineData("click", "element_id", "take element_id")]
    [InlineData("type", "element_id", "take element_id")]
    [InlineData("scroll", "element_id", "take element_id")]
    // drag's two ids are pinned with their direction in Drag_goes_from_from_element_id_to_element_id.
    [InlineData("type", "clear", "with clear and press_enter")]
    [InlineData("type", "press_enter", "with clear and press_enter")]
    [InlineData("find_element", "kind", "`find_element` with kind:text")]
    [InlineData("screenshot", "annotate", "annotate:true")]
    [InlineData("scrape", "source", "`scrape` with source dom")]
    [InlineData("powershell", "background", "background:true")]
    public void Text_parameter_is_a_parameter_of_the_tool_it_is_given_for(string tool, string parameter, string textForm)
    {
        Text.Should().Contain(textForm, "this row pins the instructions' own wording");
        var schema = ProtocolTools().Should().ContainSingle(t => t.Name == tool).Subject.InputSchema;

        schema.GetProperty("properties").TryGetProperty(parameter, out var property).Should().BeTrue(
            "the instructions tell the model to pass {0} to {1}", parameter, tool);
        if (textForm.EndsWith(":true", StringComparison.Ordinal))
            property.GetProperty("type").GetString().Should().Be("boolean", "the text passes it as {0}", textForm);
    }

    // ---- E-1 review F1: the confirm rule says which changes are gated and which never ask -------
    //
    // The rule line reads: "Some changes are refused without confirm: true. Others never ask:
    // `powershell`, `archive`, `file_manage` with overwrite, `scheduled_task` create or run,
    // `service` start. Get the user's agreement before anything destructive or persistent, gated
    // or not." Each half is proved against the tool code, not against the schema alone: a
    // parameter named confirm says nothing about which actions it guards.
    //
    // The gated half ("refused without confirm: true", service never called) is already pinned
    // per tool in the tool test classes and is cited there rather than repeated here:
    // RegistryToolsTests.RegistrySet_requires_confirm, RegistryToolsTests.RegistryDelete_requires_confirm,
    // ProcessToolsTests.Service_stop_requires_confirm_true / Service_restart_requires_confirm_true,
    // ProcessToolsTests.ScheduledTask_delete_requires_confirm_true, SystemToolsTests.PowerAction_requires_confirm,
    // FileToolsTests.FileWrite_requires_confirm, FileToolsTests.FileManage_delete_requires_confirm,
    // NetworkToolsTests.Firewall_add_requires_confirm / Firewall_remove_requires_confirm,
    // ProcessToolsTests.Process_kill_requires_confirm_true, SystemToolsTests.Env_set_requires_confirm.

    /// <summary>The one line of the text that states the confirm rule.</summary>
    private static string ConfirmRule =>
        Lines.Should().ContainSingle(l => l.Contains("confirm: true", StringComparison.Ordinal)).Subject;

    /// <summary>
    /// The items of "Others never ask: a, b, c." in the text's own words - from the colon to the
    /// end of that sentence, split at ", ".
    /// </summary>
    internal static string[] NeverAskItems(string text)
    {
        const string lead = "Others never ask: ";
        var start = text.IndexOf(lead, StringComparison.Ordinal);
        if (start < 0) return [];
        start += lead.Length;
        var end = text.IndexOf(". ", start, StringComparison.Ordinal);
        var list = end < 0 ? text[start..] : text[start..end];
        return list.Split(", ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The items <see cref="Each_never_ask_item_really_runs_without_confirm"/> has a probe for.</summary>
    private static readonly string[] ProvenNeverAskItems =
    [
        "`powershell`",
        "`archive`",
        "`file_manage` with overwrite",
        "`scheduled_task` create or run",
        "`service` start",
        "`job` cancel",
        "`window` close",
        "`http_request`",
    ];

    public static TheoryData<string> NeverAskItemData => new(ProvenNeverAskItems);

    [Fact]
    public void Confirm_rule_says_gated_changes_are_refused_and_asks_for_agreement_gated_or_not()
    {
        var rule = ConfirmRule;

        rule.Should().Contain("refused without confirm: true",
            "the gated half: a gated change is refused, not silently performed");
        rule.Should().Contain("gated or not",
            "the model must ask the person even when the server would not refuse");
        rule.Should().Contain("destructive or persistent");
    }

    /// <summary>
    /// The list in the text and the list the probes below prove are the same list: an item added
    /// to the text (say <c>`registry_set`</c>, which IS gated) has no probe and fails here, and an
    /// item dropped from the text leaves a probe proving a sentence nobody reads.
    /// </summary>
    [Fact]
    public void Never_ask_list_is_exactly_the_items_proven_ungated()
    {
        NeverAskItems(ConfirmRule).Should().BeEquivalentTo(ProvenNeverAskItems,
            "every tool the text says never asks must be shown to run without confirm by a probe below");
    }

    /// <summary>
    /// Each item of the never-ask list, driven through the real tool class with mocked services
    /// and confirm left at its default (false): the call reaches the service and does not throw.
    /// If a tool started gating one of these, the text would be telling every client that a call
    /// it will now be refused goes through.
    /// </summary>
    [Theory]
    [MemberData(nameof(NeverAskItemData))]
    public async Task Each_never_ask_item_really_runs_without_confirm(string item)
    {
        ConfirmRule.Should().Contain(item, "this row pins the instructions' own list");

        switch (item)
        {
            case "`powershell`":
                SchemaOf("powershell").GetProperty("properties").TryGetProperty("confirm", out _).Should().BeFalse(
                    "powershell has no confirm gate at all: any script runs as asked");
                break;

            case "`archive`":
                {
                    SchemaOf("archive").GetProperty("properties").TryGetProperty("confirm", out _).Should().BeFalse(
                        "archive has no confirm parameter for a caller to pass");
                    var fs = new Mock<IFileSystemService>();
                    var tools = Files(fs);
                    await tools.Archive("zip", @"C:\wmcp-e1\src", @"C:\wmcp-e1\out.zip");
                    await tools.Archive("unzip", @"C:\wmcp-e1\in.zip", @"C:\wmcp-e1\dst");
                    fs.Verify(s => s.ZipAsync(@"C:\wmcp-e1\src", @"C:\wmcp-e1\out.zip", It.IsAny<CancellationToken>()), Times.Once);
                    fs.Verify(s => s.UnzipAsync(@"C:\wmcp-e1\in.zip", @"C:\wmcp-e1\dst", It.IsAny<CancellationToken>()), Times.Once);
                    break;
                }

            case "`file_manage` with overwrite":
                {
                    var fs = new Mock<IFileSystemService>();
                    var tools = Files(fs);
                    await tools.FileManage("copy", @"C:\wmcp-e1\a.txt", @"C:\wmcp-e1\b.txt", overwrite: true);
                    await tools.FileManage("move", @"C:\wmcp-e1\c.txt", @"C:\wmcp-e1\d.txt", overwrite: true);
                    fs.Verify(s => s.CopyAsync(@"C:\wmcp-e1\a.txt", @"C:\wmcp-e1\b.txt", true, It.IsAny<CancellationToken>()), Times.Once,
                        "an overwriting copy replaces the destination with no confirm");
                    fs.Verify(s => s.MoveAsync(@"C:\wmcp-e1\c.txt", @"C:\wmcp-e1\d.txt", true, It.IsAny<CancellationToken>()), Times.Once,
                        "an overwriting move replaces the destination with no confirm");
                    break;
                }

            case "`scheduled_task` create or run":
                {
                    var scheduler = new Mock<ITaskSchedulerService>();
                    var tools = Processes(scheduler: scheduler);
                    await tools.ScheduledTask("create", name: "wmcp-e1", command: "cmd.exe /c exit", trigger: "2026-05-06T09:00:00");
                    await tools.ScheduledTask("run", name: "wmcp-e1");
                    scheduler.Verify(s => s.CreateAsync("wmcp-e1", "cmd.exe /c exit", "2026-05-06T09:00:00", It.IsAny<CancellationToken>()), Times.Once);
                    scheduler.Verify(s => s.RunAsync("wmcp-e1", It.IsAny<CancellationToken>()), Times.Once);
                    break;
                }

            case "`service` start":
                {
                    var service = new Mock<IServiceControlService>();
                    await Processes(service: service).Service("start", name: "Spooler");
                    service.Verify(s => s.StartAsync("Spooler", It.IsAny<CancellationToken>()), Times.Once);
                    break;
                }

            case "`job` cancel":
                {
                    NoConfirmIn("job");
                    var jobs = new Mock<IJobService>();
                    jobs.Setup(s => s.Cancel("j1")).Returns(true);
                    var reply = new JobTools(jobs.Object).Job("cancel", id: "j1");
                    jobs.Verify(s => s.Cancel("j1"), Times.Once, "cancel kills the job's process tree with no confirm");
                    reply.Should().Contain("\"cancelled\":true");
                    break;
                }

            case "`window` close":
                {
                    NoConfirmIn("window");
                    var windows = new Mock<IWindowService>();
                    windows.Setup(s => s.ExecuteAsync("close", "Untitled - Notepad", null, It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new WindowAction("close", "Untitled - Notepad", true, "exact", 100, 0x99));
                    await new WindowTools(windows.Object, new Mock<IVirtualDesktopService>().Object)
                        .Window("close", title: "Untitled - Notepad");
                    windows.Verify(s => s.ExecuteAsync("close", "Untitled - Notepad", null, It.IsAny<CancellationToken>()), Times.Once,
                        "a window is closed with no confirm");
                    break;
                }

            case "`http_request`":
                {
                    NoConfirmIn("http_request");
                    var web = new Mock<IWebService>();
                    web.Setup(s => s.RequestAsync(It.IsAny<string>(), It.IsAny<string>(),
                            It.IsAny<IDictionary<string, string>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new HttpResponseDto(204, new Dictionary<string, string>(), ""));
                    var tools = new WebTools(web.Object, new Mock<IUIAutomationService>().Object, new Mock<IWindowService>().Object);
                    await tools.HttpRequest("https://example.test/items/1", method: "DELETE");
                    await tools.HttpRequest("https://example.test/items", method: "POST", body: "{\"name\":\"x\"}");
                    web.Verify(s => s.RequestAsync("https://example.test/items/1", "DELETE", null, null, It.IsAny<CancellationToken>()), Times.Once,
                        "a mutating request goes out with no confirm");
                    web.Verify(s => s.RequestAsync("https://example.test/items", "POST", null, "{\"name\":\"x\"}", It.IsAny<CancellationToken>()), Times.Once);
                    break;
                }

            default:
                throw new InvalidOperationException($"no probe for never-ask item {item}");
        }
    }

    /// <summary>
    /// The never-ask list read as what it looks like - the complete list of changes the server
    /// performs without confirm. Every tool annotated destructive that has no confirm parameter
    /// at all must be on it, or the model reads the list, does not find the tool, and concludes
    /// the server would have refused it.
    /// </summary>
    [Fact]
    public void Every_destructive_tool_without_a_confirm_parameter_is_on_the_never_ask_list()
    {
        var named = BacktickedIdentifiers(string.Join(", ", NeverAskItems(ConfirmRule)));
        var ungated = ProtocolTools()
            .Where(t => t.Annotations?.DestructiveHint == true)
            .Where(t => !t.InputSchema.GetProperty("properties").TryGetProperty("confirm", out _))
            .Select(t => t.Name)
            .ToArray();

        ungated.Should().Contain(["archive", "powershell"],
            "both are destructive with no confirm, so the check is not vacuous");
        ungated.Should().BeSubsetOf(named,
            "the text lists the changes that never ask, and each of these is a destructive tool with no confirm gate");
    }

    [Fact]
    public void Gated_and_never_ask_tool_schemas_agree_with_the_rule()
    {
        // The schema half: the tools the gated tests above refuse really advertise a boolean
        // confirm, and the never-ask tools that have one (file_manage, scheduled_task, service)
        // use it only for other actions - their probes above show the named actions ignore it.
        foreach (var tool in new[] { "registry_set", "registry_delete", "service", "scheduled_task", "power_action",
                     "file_write", "file_manage", "firewall", "process", "env" })
        {
            SchemaOf(tool).GetProperty("properties").TryGetProperty("confirm", out var confirm).Should().BeTrue(
                "{0} has a gated action, so it takes confirm", tool);
            confirm.GetProperty("type").GetString().Should().Be("boolean", "the text passes it as confirm: true");
        }
    }

    // ---- E-1 review F2: drag's direction --------------------------------------------------------

    /// <summary>
    /// "`drag` goes from from_element_id to element_id": both are parameters, and a drag given both
    /// ids presses at the first one's centre and releases at the second's. The old text said
    /// element_id was what drag "takes", which reads as the thing dragged - the origin.
    /// </summary>
    [Fact]
    public async Task Drag_goes_from_from_element_id_to_element_id()
    {
        Text.Should().Contain("`drag` goes from from_element_id to element_id");
        var properties = SchemaOf("drag").GetProperty("properties");
        properties.TryGetProperty("from_element_id", out _).Should().BeTrue();
        properties.TryGetProperty("element_id", out _).Should().BeTrue();

        var input = new Mock<IInputService>();
        input.Setup(s => s.DragAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int fx, int fy, int tx, int ty, MouseButton b, int _, int __, CancellationToken ___)
                => new DragResult(fx, fy, tx, ty, b));
        var uia = new Mock<IUIAutomationService>();
        // Origin centre (10,5); destination centre (120,210).
        uia.Setup(s => s.GetElementAsync("el_3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_3", name: "report.txt", x: 0, y: 0, width: 20, height: 10));
        uia.Setup(s => s.GetElementAsync("el_12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_12", name: "Trash"));

        var json = InputVerb.Json(await InputVerb.Tools(input, uia).Drag(from_element_id: "el_3", element_id: "el_12"));

        input.Verify(s => s.DragAsync(10, 5, 120, 210, MouseButton.Left, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once, "pressed on from_element_id, released on element_id");
        InputVerb.Str(json, "fromTarget").Should().Be("element");
        InputVerb.Str(json, "elementId").Should().Be("el_12", "the reply's element is the destination");
    }

    // ---- E-1 review F3: what a snapshot row is ------------------------------------------------

    [Fact]
    public void Text_says_snapshot_lists_the_interactive_controls_not_every_control()
    {
        Text.Should().Contain("every interactive control as el_N (x,y) type \"name\" [action: ...]");
        Text.Should().NotContain("every control", "a label or a static text is never a snapshot row");
        Text.Should().Contain("Labels and plain text are not rows");
    }

    /// <summary>
    /// The behaviour behind it, through the pure seam the service uses for every walked node
    /// (<c>UIAutomationService.Project</c>, then <c>SnapshotRenderer.Render</c>): a Text control -
    /// a label - projects to no interactive row, so the rendered snapshot has no id for it, while
    /// a button renders as <c>el_N (x,y) type "name"  [action: click]</c>, the shape the text quotes.
    /// </summary>
    [Fact]
    public void A_label_gets_no_snapshot_row_while_a_button_gets_the_row_the_text_quotes()
    {
        var label = NodeFixtures.Node("Text", name: "Status: Ready", bounds: new Bounds(10, 10, 100, 20));
        var button = NodeFixtures.Node("Button", name: "Save");

        var (labelRow, labelRegion) = UIAutomationService.Project(label, "el_1");
        var (buttonRow, _) = UIAutomationService.Project(button, "el_2");

        labelRow.Should().BeNull("a label is not interactive");
        labelRegion.Should().BeNull("and it does not scroll");
        buttonRow.Should().NotBeNull();
        var rendered = SnapshotRenderer.Render(new SnapshotResult(
            [], null, new CursorPosition(0, 0), -1, [buttonRow!], [], null, false, 500, 2, 0));

        rendered.Split('\n').Should().Contain(l => Regex.IsMatch(l, "^  el_2 \\(612,388\\) button \"Save\"  \\[action: click\\]$"),
            "the row shape is el_N (x,y) type \"name\" [action: ...]");
        rendered.Should().NotContain("el_1").And.NotContain("Status: Ready");
    }

    // ---- E-1 review: Satisfied:false is what the TOOL returns, not what a record serialises -----

    /// <summary>
    /// "a timeout is Satisfied:false, not an error" - driven through <c>wait_for</c> itself with a
    /// service that timed out: the call returns (no throw) and the string the client receives
    /// carries the field the text names. UIAutomationToolsWaitForTests
    /// .WaitFor_returns_a_result_on_timeout_instead_of_the_string_null pins the rest of the shape.
    /// </summary>
    [Fact]
    public async Task A_timed_out_wait_for_returns_Satisfied_false_instead_of_throwing()
    {
        Text.Should().Contain("a timeout is Satisfied:false, not an error");
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.WaitForAsync(It.IsAny<WaitRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WaitForResult(false, "text_exists", 10000, 20, "'Ready' not found anywhere on screen"));

        var reply = await new UIAutomationTools(uia.Object).WaitFor("Ready", condition: "text");

        reply.Should().Contain("\"Satisfied\":false", "the instructions name the field as it appears on the wire");
        uia.Verify(s => s.WaitForAsync(It.Is<WaitRequest>(r => r.Condition == WaitCondition.TextExists && r.Text == "Ready"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- E-1 review R-F4: what evicts snapshot ids ----------------------------------------------

    /// <summary>
    /// "Snapshot ids die at the next `snapshot`, and at `screenshot` with annotate:true, `wait_for`
    /// on text or focus, and `scrape` with source dom." The behaviour, both directions, is pinned
    /// where each tool is tested:
    /// <list type="bullet">
    /// <item>screenshot: ScreenToolsTests.Screenshot_annotate_takes_exactly_one_desktop_snapshot /
    /// Screenshot_without_annotate_never_walks_the_desktop.</item>
    /// <item>scrape: WebToolsTests.Scrape_dom_with_a_window_walks_that_window_and_never_lists_the_desktop /
    /// Scrape_http_never_walks_the_desktop.</item>
    /// <item>wait_for: WaitForServiceTests.Text_and_focus_waits_take_a_snapshot_on_every_poll /
    /// Active_window_reads_the_inventory_and_nothing_else, and
    /// WaitForFindPathIntegrationTests.Element_waits_never_take_a_snapshot (Integration: the find
    /// path reads the live top-level window list).</item>
    /// </list>
    /// This row pins that the text names exactly those evictors, so a new one has to be added here
    /// and given its own pair of tests.
    /// </summary>
    [Fact]
    public void Text_names_each_call_that_replaces_snapshot_ids()
    {
        var line = Lines.Should().ContainSingle(l => l.Contains("Snapshot ids die", StringComparison.Ordinal)).Subject;

        line.Should().Contain("the next `snapshot`");
        line.Should().Contain("`screenshot` with annotate:true");
        line.Should().Contain("`wait_for` on text or focus");
        line.Should().Contain("`scrape` with source dom");
        BacktickedIdentifiers(line).Should().BeEquivalentTo(["snapshot", "screenshot", "wait_for", "scrape"],
            "the four calls that walk through SnapshotAsync, and no other tool");
    }

    // ---- helpers for the behaviour rows ---------------------------------------------------------

    private static JsonElement SchemaOf(string tool) =>
        ProtocolTools().Should().ContainSingle(t => t.Name == tool).Subject.InputSchema;

    private static void NoConfirmIn(string tool) =>
        SchemaOf(tool).GetProperty("properties").TryGetProperty("confirm", out _).Should().BeFalse(
            "{0} has no confirm parameter for a caller to pass", tool);

    private static FileTools Files(Mock<IFileSystemService> fs) =>
        new(fs.Object, new Mock<IInputService>().Object, new Mock<IFileStreamService>().Object);

    private static ProcessTools Processes(
        Mock<IServiceControlService>? service = null, Mock<ITaskSchedulerService>? scheduler = null) =>
        new(new Mock<IProcessService>().Object,
            (service ?? new Mock<IServiceControlService>()).Object,
            (scheduler ?? new Mock<ITaskSchedulerService>()).Object,
            new Mock<IEventLogService>().Object);

    /// <summary>
    /// "Truncated in a result means there is more" - the default (text) snapshot says it with that
    /// word, and the json form carries the field of that name.
    /// </summary>
    [Fact]
    public void Truncated_is_the_word_a_capped_snapshot_uses_in_both_formats()
    {
        Text.Should().Contain("Truncated in a result");

        WindowsMcp.Services.UiTree.ElementBudget.NoteFor(500).Should().StartWith("Truncated",
            "snapshot's default text form ends a capped walk with this note");
        typeof(SnapshotResult).GetProperty("Truncated").Should().NotBeNull("format:json carries the field by that name");
    }

    // ---- R5: length ceiling --------------------------------------------------------------------

    /// <summary>
    /// The instructions are paid for in every session, and clients cap what they keep (Claude Code
    /// truncates server instructions at about 2 KB), so anything past the ceiling is silently lost
    /// - the tail of the loop would simply never reach the model.
    /// </summary>
    [Fact]
    public void Text_fits_in_2000_characters_and_40_lines()
    {
        Text.Should().NotBeEmpty("a ceiling over an empty text proves nothing");
        Text.Length.Should().BeLessThanOrEqualTo(2000,
            "Claude Code keeps about 2 KB of server instructions; the rest is silently dropped");
        Lines.Length.Should().BeLessThanOrEqualTo(40, "it is paid for in every session - keep it short");
    }

    // ---- R6: wire-safe -------------------------------------------------------------------------

    /// <summary>
    /// Printable ASCII and LF only. A multi-line raw string literal takes its line breaks from the
    /// source file, and this repo checks out CRLF (core.autocrlf=true), so a <c>\r</c> here is the
    /// expected way for this to break; tabs and non-ASCII punctuation are the others.
    /// </summary>
    [Fact]
    public void Text_is_printable_ascii_with_lf_line_breaks_only()
    {
        Text.Should().NotBeEmpty();
        var offending = Text
            .Select((c, i) => (c, i))
            .Where(x => x.c != '\n' && (x.c < (char)0x20 || x.c > (char)0x7E))
            .Select(x => $"U+{(int)x.c:X4} at {x.i}")
            .ToArray();

        offending.Should().BeEmpty("only 0x20-0x7E and \\n are allowed: no \\r, no tab, no smart quotes or dashes");
    }

    [Fact]
    public void No_line_has_trailing_whitespace()
    {
        Text.Should().NotBeEmpty();
        Lines.Select((line, i) => (line, i))
            .Where(x => x.line.Length > 0 && char.IsWhiteSpace(x.line[^1]))
            .Select(x => $"line {x.i + 1}")
            .Should().BeEmpty();
    }

    [Fact]
    public void Text_has_no_leading_or_trailing_blank_line()
    {
        Lines[0].Should().NotBeNullOrWhiteSpace("the first line is the identity, not padding");
        Lines[^1].Should().NotBeNullOrWhiteSpace("no trailing blank line, and no trailing line break");
    }

    // ---- R7: the identity comes first ----------------------------------------------------------

    [Fact]
    public void First_line_says_what_the_server_is()
    {
        Lines[0].Should().Contain("Windows",
            "a truncated read still starts with what this server is");
    }
}
