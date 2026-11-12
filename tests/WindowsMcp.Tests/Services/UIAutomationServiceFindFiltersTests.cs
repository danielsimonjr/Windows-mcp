using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using Moq.Protected;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-4 (RED): <c>find_element</c>'s filters (automation id, control type, class name), <c>within</c>
/// and paging, driven through the REAL <see cref="UIAutomationService"/> over a FAKE element tree,
/// headless. <c>within</c> is what makes this possible: it roots the search at an id the cache
/// already holds, so a fake root put in the cache by reflection (the
/// <c>UIAutomationServiceRefusingTests</c> trick) is walked instead of a live window.
/// <para>
/// Each fake is a <c>Mock&lt;FrameworkAutomationElementBase&gt;</c> behind a real
/// <see cref="AutomationElement"/>: every property read goes through FlaUI's own conversion, and
/// <c>FindAll</c>/<c>FindFirst</c> answer from the fake tree by EVALUATING whatever condition the
/// service passes (property, and, or, not, true/false; string flags honoured) against the fakes'
/// converted properties. So the service is free to push a filter into the UIA condition or apply it
/// client-side — both are observed the same way. What the harness cannot follow is a walk through
/// a <c>TreeWalker</c> (it goes to the native automation, not the element); the live proof that is
/// independent of how the walk is done is <see cref="UIAutomationServiceFindFiltersIntegrationTests"/>.
/// Anything the harness does not understand is recorded in <see cref="_harnessErrors"/> and fails
/// the row loudly instead of turning into a silent empty result.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationServiceFindFiltersTests : IDisposable
{
    // UIA control type ids (raw property values).
    private const int ButtonType = 50000;
    private const int EditType = 50004;
    private const int ListItemType = 50007;
    private const int ListType = 50008;
    private const int TreeItemType = 50024;
    private const int GroupType = 50026;
    private const int PaneType = 50033;

    private readonly UIA3Automation _automation = new();
    private readonly UIAutomationService _svc;
    private readonly List<string> _harnessErrors = new();

    public UIAutomationServiceFindFiltersTests()
    {
        // STRICT: a within-rooted find needs neither the pointer nor the window inventory.
        _svc = new UIAutomationService(
            new Mock<IInputService>(MockBehavior.Strict).Object,
            new Mock<IWindowService>(MockBehavior.Strict).Object);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _automation.Dispose();
    }

    // ============================================================================================
    // Harness
    // ============================================================================================

    private sealed class Node
    {
        public string Name { get; init; } = "";
        public int ControlType { get; init; } = ButtonType;
        public string AutomationId { get; init; } = "";
        public string ClassName { get; init; } = "";
        public bool Enabled { get; init; } = true;
        public bool Offscreen { get; init; }
        public double[] Rect { get; init; } = [100, 100, 40, 20];
        /// <summary>The ProcessId read; null = a live process (4242).</summary>
        public Func<object>? ProcessId { get; set; }
        /// <summary>Thrown by every Find* on this element.</summary>
        public Exception? FindThrows { get; set; }
        public List<Node> Children { get; init; } = new();
        /// <summary>Extra descendants sharing one framework (big trees).</summary>
        public List<AutomationElement> Bulk { get; } = new();
        public Mock<FrameworkAutomationElementBase> Framework { get; set; } = null!;
        public AutomationElement Element { get; set; } = null!;
        public int FindCalls { get; set; }
        /// <summary>
        /// Name reads on this node (on the shared template, for bulk rows). With a non-empty text
        /// every candidate the service examines costs one Name read first, so this counts
        /// candidates examined (plus one per <c>ToInfo</c> of a page entry).
        /// </summary>
        public int NameReads { get; set; }
    }

    /// <summary>Called on every Name read, on the STA thread, after the count is bumped (R4's cancel hook).</summary>
    private Action<Node>? _onNameRead;

    /// <summary>R3b / R1: the desktop's top-level windows for scope=desktop, through the service's seam.</summary>
    private void Desktop(params Node[] windows) =>
        _svc.DesktopWindows = () => windows.Select(w => w.Element).ToArray();

    private static FindElementRequest OnDesktop(string text = "", string? automationId = null, string? className = null,
        string? controlType = null, int maxResults = 20, int offset = 0)
        => new(text, FindKind.Any, FindScope.Desktop, AutomationId: automationId, ClassName: className,
            ControlType: controlType, MaxResults: maxResults, Offset: offset);

    /// <summary>A top-level window with <paramref name="rows"/> list items named "{prefix} Row nn".</summary>
    private Node TopWindow(string prefix, int rows) => Build(
        N($"Win {prefix}", PaneType, $"win{prefix}", "Window",
            children: Enumerable.Range(0, rows).Select(i => N($"{prefix} Row {i:00}", ListItemType, $"{prefix}{i:00}", "Row")).ToArray()));

    private object NotSupported => _automation.NotSupportedValue;
    private IPropertyLibrary Lib => _automation.PropertyLibrary;

    private ElementCache<AutomationElement> Cache => (ElementCache<AutomationElement>)typeof(UIAutomationService)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(_svc)!;

    private static Node N(string name, int type = ButtonType, string aid = "", string cls = "",
        bool enabled = true, bool offscreen = false, params Node[] children)
        => new()
        {
            Name = name,
            ControlType = type,
            AutomationId = aid,
            ClassName = cls,
            Enabled = enabled,
            Offscreen = offscreen,
            Children = children.ToList()
        };

    /// <summary>Builds the fakes for <paramref name="root"/> and its subtree; returns the root.</summary>
    private Node Build(Node root)
    {
        Wire(root);
        foreach (var child in root.Children) Build(child);
        return root;
    }

    private void Wire(Node node)
    {
        var framework = new Mock<FrameworkAutomationElementBase>(MockBehavior.Loose, _automation);
        var el = Lib.Element;

        object Read(int id)
        {
            if (id == el.Name.Id)
            {
                node.NameReads++;
                _onNameRead?.Invoke(node);
                return node.Name;
            }
            if (id == el.ControlType.Id) return node.ControlType;
            if (id == el.AutomationId.Id) return node.AutomationId;
            if (id == el.ClassName.Id) return node.ClassName;
            if (id == el.IsEnabled.Id) return node.Enabled;
            if (id == el.IsOffscreen.Id) return node.Offscreen;
            if (id == el.BoundingRectangle.Id) return node.Rect;
            if (id == el.ProcessId.Id) return node.ProcessId?.Invoke() ?? 4242;
            if (id == el.IsPassword.Id) return false;
            return NotSupported;
        }

        framework.Protected()
            .Setup<object>("InternalGetPropertyValue", ItExpr.IsAny<int>(), ItExpr.IsAny<bool>(), ItExpr.IsAny<bool>())
            .Returns((int id, bool _, bool _) => Read(id));
        framework.Setup(f => f.FindAll(It.IsAny<TreeScope>(), It.IsAny<ConditionBase>()))
            .Returns((TreeScope s, ConditionBase c) => Find(node, s, c));
        framework.Setup(f => f.FindAllWithOptions(It.IsAny<TreeScope>(), It.IsAny<ConditionBase>(),
                It.IsAny<TreeTraversalOptions>(), It.IsAny<AutomationElement>()))
            .Returns((TreeScope s, ConditionBase c, TreeTraversalOptions _, AutomationElement _) => Find(node, s, c));
        framework.Setup(f => f.FindFirst(It.IsAny<TreeScope>(), It.IsAny<ConditionBase>()))
            .Returns((TreeScope s, ConditionBase c) => Find(node, s, c).FirstOrDefault()!);
        framework.Setup(f => f.FindFirstWithOptions(It.IsAny<TreeScope>(), It.IsAny<ConditionBase>(),
                It.IsAny<TreeTraversalOptions>(), It.IsAny<AutomationElement>()))
            .Returns((TreeScope s, ConditionBase c, TreeTraversalOptions _, AutomationElement _) => Find(node, s, c).FirstOrDefault()!);
        framework.Setup(f => f.FindAt(It.IsAny<TreeScope>(), It.IsAny<int>(), It.IsAny<ConditionBase>()))
            .Returns((TreeScope s, int i, ConditionBase c) => Find(node, s, c).ElementAtOrDefault(i)!);

        Absent<IValuePattern>(framework, "InitializeValuePattern");
        Absent<ITogglePattern>(framework, "InitializeTogglePattern");
        Absent<ISelectionItemPattern>(framework, "InitializeSelectionItemPattern");
        Absent<IScrollPattern>(framework, "InitializeScrollPattern");
        Absent<IInvokePattern>(framework, "InitializeInvokePattern");
        Absent<IExpandCollapsePattern>(framework, "InitializeExpandCollapsePattern");
        Absent<IRangeValuePattern>(framework, "InitializeRangeValuePattern");
        Absent<IScrollItemPattern>(framework, "InitializeScrollItemPattern");

        node.Framework = framework;
        node.Element = new AutomationElement(framework.Object);
    }

    private static void Absent<T>(Mock<FrameworkAutomationElementBase> framework, string initializer)
        where T : class, FlaUI.Core.Patterns.Infrastructure.IPattern
    {
        var pattern = new Mock<IAutomationPattern<T>>();
        pattern.SetupGet(p => p.PatternOrDefault).Returns((T)null!);
        pattern.SetupGet(p => p.IsSupported).Returns(false);
        T? none = null;
        pattern.Setup(p => p.TryGetPattern(out none)).Returns(false);
        pattern.SetupGet(p => p.Pattern).Throws(new FlaUI.Core.Exceptions.PatternNotSupportedException());
        framework.Protected().Setup<IAutomationPattern<T>>(initializer).Returns(pattern.Object);
    }

    /// <summary>Adds <paramref name="count"/> identical descendants that share ONE fake framework.</summary>
    private void AddBulk(Node parent, int count, Node template)
    {
        Wire(template);
        for (int i = 0; i < count; i++) parent.Bulk.Add(new AutomationElement(template.Framework.Object));
    }

    private static IEnumerable<AutomationElement> Descendants(Node node)
    {
        foreach (var child in node.Children)
        {
            yield return child.Element;
            foreach (var d in Descendants(child)) yield return d;
        }
        foreach (var b in node.Bulk) yield return b;
    }

    private AutomationElement[] Find(Node node, TreeScope scope, ConditionBase condition)
    {
        node.FindCalls++;
        if (node.FindThrows is { } ex) throw ex;
        var pool = new List<AutomationElement>();
        if (scope.HasFlag(TreeScope.Element)) pool.Add(node.Element);
        if (scope.HasFlag(TreeScope.Descendants)) pool.AddRange(Descendants(node));
        else if (scope.HasFlag(TreeScope.Children)) pool.AddRange(node.Children.Select(c => c.Element).Concat(node.Bulk));
        if ((scope & ~TreeScope.Subtree) != 0)
            _harnessErrors.Add($"fake tree: unsupported TreeScope {scope}");
        return pool.Where(e => Matches(e, condition)).ToArray();
    }

    private bool Matches(AutomationElement el, ConditionBase condition)
    {
        switch (condition)
        {
            case BoolCondition b: return b.BooleanValue;
            case AndCondition a: return a.Conditions.All(c => Matches(el, c));
            case OrCondition o: return o.Conditions.Any(c => Matches(el, c));
            case NotCondition n: return !Matches(el, n.Condition);
            case PropertyCondition p:
                {
                    if (!el.FrameworkAutomationElement.TryGetPropertyValue(p.Property, out object? actual) || actual is null)
                        return false;
                    if (actual is string s && p.Value is string want)
                    {
                        var cmp = p.PropertyConditionFlags.HasFlag(PropertyConditionFlags.IgnoreCase)
                            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        return p.PropertyConditionFlags.HasFlag(PropertyConditionFlags.MatchSubstring)
                            ? s.Contains(want, cmp) : string.Equals(s, want, cmp);
                    }
                    if (p.Value is not null && actual.GetType() != p.Value.GetType())
                        _harnessErrors.Add($"fake tree: condition on {p.Property.Name} compares {p.Value.GetType().Name} with {actual.GetType().Name}");
                    return Equals(actual, p.Value);
                }
            default:
                _harnessErrors.Add($"fake tree: unsupported condition {condition.GetType().Name}");
                return false;
        }
    }

    private string Issue(Node node) => Cache.Add(node.Element);

    private async Task<FindElementResult> FindAsync(FindElementRequest request)
    {
        var result = await _svc.FindElementAsync(request);
        _harnessErrors.Should().BeEmpty("the fake tree understood every call the service made");
        return result;
    }

    private static string?[] Ids(FindElementResult r) => r.Matches.Select(m => m.AutomationId).ToArray();

    /// <summary>
    /// A dialog: root pane, two buttons with the SAME name, an unnamed toolbar button, an edit, and
    /// a group holding a button and a tree item. Walk order is the order written here.
    /// </summary>
    private Node Dialog(bool saveAsEnabled = true) => Build(
        N("Dialog", PaneType, "dlg", "#32770", children:
        [
            N("Save", ButtonType, "btnSave", "Button"),
            N("Save", ButtonType, "btnSaveAs", "Button", enabled: saveAsEnabled),
            N("", ButtonType, "tbNew", "ToolbarButton"),
            N("Search", EditType, "txtSearch", "Edit"),
            N("Options", GroupType, "grpOptions", "Button", children:
            [
                N("Apply", ButtonType, "btnApply", "Button"),
                N("Node", TreeItemType, "tiNode", "SysTreeView32Item"),
            ]),
        ]));

    /// <summary>A list root with <paramref name="rows"/> named rows "Row 00".. in walk order.</summary>
    private Node List(int rows) => Build(
        N("Rows", ListType, "lst", "ListBox",
            children: Enumerable.Range(0, rows).Select(i => N($"Row {i:00}", ListItemType, $"row{i:00}", "")).ToArray()));

    private static Node Child(Node root, string aid) => Flatten(root).Single(n => n.AutomationId == aid);

    private static IEnumerable<Node> Flatten(Node node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var d in Flatten(child)) yield return d;
    }

    // ============================================================================================
    // Harness self-test (green before E-4): the fake tree answers FlaUI's own searches correctly,
    // so a red row below is the service, not the harness.
    // ============================================================================================

    [Fact]
    public void Harness_answers_FlaUI_searches_by_evaluating_the_condition()
    {
        var root = Dialog();
        var cf = _automation.ConditionFactory;
        string[] Aids(AutomationElement[] found) => found.Select(e => e.Properties.AutomationId.Value).ToArray();

        Aids(root.Element.FindAllDescendants()).Should().Equal("btnSave", "btnSaveAs", "tbNew", "txtSearch", "grpOptions", "btnApply", "tiNode");
        Aids(root.Element.FindAllChildren()).Should().Equal("btnSave", "btnSaveAs", "tbNew", "txtSearch", "grpOptions");
        Aids(root.Element.FindAllDescendants(cf.ByAutomationId("btnApply"))).Should().Equal("btnApply");
        Aids(root.Element.FindAllDescendants(cf.ByClassName("button", PropertyConditionFlags.IgnoreCase)))
            .Should().Equal("btnSave", "btnSaveAs", "grpOptions", "btnApply");
        Aids(root.Element.FindAllDescendants(cf.ByControlType(ControlType.Button).And(cf.ByName("Save"))))
            .Should().Equal("btnSave", "btnSaveAs");
        Aids(root.Element.FindAllDescendants(new OrCondition(cf.ByControlType(ControlType.Edit), cf.ByControlType(ControlType.TreeItem))))
            .Should().Equal("txtSearch", "tiNode");
        Aids(root.Element.FindAll(TreeScope.Subtree, cf.ByControlType(ControlType.Pane))).Should().Equal("dlg");
        root.Element.ControlType.Should().Be(ControlType.Pane);
        root.Element.BoundingRectangle.Width.Should().Be(40);
        _harnessErrors.Should().BeEmpty();
    }

    // ============================================================================================
    // F5 / D1: AutomationId and ClassName on every ElementInfo
    // ============================================================================================

    [Fact]
    public async Task Find_reports_the_automation_id_and_class_name_of_each_match()
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest("Save", Within: Issue(root)));

        result.Matches.Select(m => (m.Name, m.AutomationId, m.ClassName)).Should().Equal(
            ("Save", "btnSave", "Button"), ("Save", "btnSaveAs", "Button"));
    }

    [Fact]
    public async Task Find_stores_an_empty_or_sanitised_away_automation_id_and_class_name_as_null()
    {
        var root = Build(N("Root", PaneType, "", "", children:
        [
            N("Blank", ButtonType, "", ""),
            N("Noise", ButtonType, "\u0007", ""),
        ]));

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "Button"));

        result.Matches.Should().HaveCount(2);
        result.Matches.Should().OnlyContain(m => m.AutomationId == null && m.ClassName == null,
            "an empty string from the provider, or one the sanitiser empties, is stored as null so JSON omits it");
    }

    [Fact]
    public async Task Find_sanitises_the_automation_id_and_class_name_like_the_name()
    {
        var root = Build(N("Root", PaneType, children:
        [
            N("Target", ButtonType, "btn\u0007Save", "Bu\u0000tton"),
        ]));

        var result = await FindAsync(new FindElementRequest("Target", Within: Issue(root)));

        var match = result.Matches.Should().ContainSingle().Subject;
        match.AutomationId.Should().Be(UiText.Sanitize("btn\u0007Save")).And.Be("btnSave");
        match.ClassName.Should().Be("Button");
    }

    [Fact]
    public async Task GetElementAsync_reports_the_automation_id_and_class_name()
    {
        var root = Dialog();
        var save = Child(root, "btnSave");
        var id = Issue(save);

        var info = await _svc.GetElementAsync(id);

        info.ElementId.Should().Be(id);
        info.AutomationId.Should().Be("btnSave");
        info.ClassName.Should().Be("Button");
    }

    [Fact]
    public async Task GetElementAsync_reports_an_empty_automation_id_and_class_name_as_null()
    {
        // D1 on the lookup path too: get_element's JSON omits what the provider left empty. GREEN
        // before E-4 (nothing is read yet, so both are null); it bites an implementation that
        // stores the provider's "" instead of null.
        var root = Build(N("Root", PaneType, children: [N("Blank", ButtonType, "", "\u0007")]));
        var id = Issue(root.Children[0]);

        var info = await _svc.GetElementAsync(id);

        info.Name.Should().Be("Blank", "precondition: the lookup read the fake");
        info.AutomationId.Should().BeNull();
        info.ClassName.Should().BeNull("a class name the sanitiser empties is stored as null");
    }

    // ============================================================================================
    // F1: each filter alone
    // ============================================================================================

    [Theory]
    [InlineData("btnSave", new[] { "btnSave" })]
    [InlineData("btnSaveAs", new[] { "btnSaveAs" })]
    [InlineData("BTNSAVE", new string[0])]      // case-sensitive
    [InlineData("btnSav", new string[0])]       // exact, not a prefix
    [InlineData("tnSave", new string[0])]       // exact, not a substring
    public async Task Automation_id_matches_exactly_and_case_sensitively(string automationId, string[] expected)
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: automationId));

        Ids(result).Should().Equal(expected);
        result.Total.Should().Be(expected.Length);
    }

    [Theory]
    [InlineData("Button", new[] { "btnSave", "btnSaveAs", "grpOptions", "btnApply" })]
    [InlineData("button", new[] { "btnSave", "btnSaveAs", "grpOptions", "btnApply" })]   // case-insensitive
    [InlineData("BUTTON", new[] { "btnSave", "btnSaveAs", "grpOptions", "btnApply" })]
    [InlineData("edit", new[] { "txtSearch" })]
    [InlineData("Butt", new string[0])]                                                  // exact, not a prefix
    [InlineData("ToolbarButtonX", new string[0])]
    public async Task Class_name_matches_exactly_and_case_insensitively(string className, string[] expected)
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), ClassName: className));

        Ids(result).Should().Equal(expected);
    }

    [Theory]
    [InlineData("Button", new[] { "btnSave", "btnSaveAs", "tbNew", "btnApply" })]
    [InlineData("button", new[] { "btnSave", "btnSaveAs", "tbNew", "btnApply" })]
    [InlineData("TreeItem", new[] { "tiNode" })]
    [InlineData("treeitem", new[] { "tiNode" })]
    [InlineData("Group", new[] { "grpOptions" })]
    [InlineData("edit", new[] { "txtSearch" })]
    [InlineData("pane", new string[0])]                   // RE-PINNED (R2): the only Pane is the within element, never a match
    public async Task Control_type_matches_the_UIA_type_name_case_insensitively(string controlType, string[] expected)
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: controlType));

        Ids(result).Should().Equal(expected);
    }

    [Theory]
    [InlineData("Buton")]
    [InlineData("Btn")]
    [InlineData("Interactive")]   // a find KIND, not a UIA control type
    public async Task An_unknown_control_type_is_refused_listing_the_valid_names_before_any_walk(string controlType)
    {
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: id, ControlType: controlType));

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().BeOneOf("controlType", "control_type", "ControlType");
        ex.Message.Should().Contain(controlType, "the refusal quotes what the caller sent");
        ex.Message.Should().Contain("Button").And.Contain("TreeItem").And.Contain("ListItem").And.Contain("Edit",
            "the refusal lists the valid UIA control type names");
        root.FindCalls.Should().Be(0, "an argument error is decided before the tree is walked");
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("ListItem")]
    [InlineData("TreeItem")]
    [InlineData("DataItem")]
    [InlineData("MenuItem")]
    [InlineData("Window")]
    [InlineData("Document")]
    [InlineData("Hyperlink")]
    [InlineData("SplitButton")]
    [InlineData("TitleBar")]
    public async Task Every_control_type_name_find_element_reports_is_accepted_as_a_filter(string controlType)
    {
        // ElementInfo.ControlType is FlaUI's ControlType name; a name read off one result must be
        // usable as the filter of the next call.
        var root = Dialog();

        Func<Task> act = () => FindAsync(new FindElementRequest(Within: Issue(root), ControlType: controlType));

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData(null, "", null)]
    [InlineData(null, " \t", null)]
    [InlineData(null, null, "")]
    [InlineData(null, null, "  ")]
    public async Task A_blank_filter_means_no_filter(string? automationId, string? className, string? controlType)
    {
        var root = Dialog();
        var id = Issue(root);
        var unfiltered = await FindAsync(new FindElementRequest(Within: id));

        var blank = await FindAsync(new FindElementRequest(Within: id,
            AutomationId: automationId, ClassName: className, ControlType: controlType));

        Ids(blank).Should().Equal(Ids(unfiltered));
        blank.Total.Should().Be(7, "the seven descendants, nothing filtered; the within element itself is not a candidate (R2)");
    }

    // ============================================================================================
    // F2: filters combine (AND) with each other, with text, kind and visibility
    // ============================================================================================

    [Fact]
    public async Task Control_type_and_class_name_together_narrow_to_what_satisfies_both()
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "Button", ClassName: "Button"));

        // tbNew is a Button of another class; grpOptions is of class Button but a Group.
        Ids(result).Should().Equal("btnSave", "btnSaveAs", "btnApply");
    }

    [Fact]
    public async Task Text_and_a_filter_combine()
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest("Save", Within: Issue(root), AutomationId: "btnSaveAs"));

        Ids(result).Should().Equal("btnSaveAs");
    }

    [Fact]
    public async Task A_combination_nothing_satisfies_is_an_empty_page_with_Total_zero()
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: "btnApply", ControlType: "Edit"));

        result.Matches.Should().BeEmpty();
        result.Total.Should().Be(0);
        result.Truncated.Should().BeFalse();
        result.TotalIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public async Task Kind_and_a_filter_combine()
    {
        var root = Dialog();
        var id = Issue(root);

        var interactive = await FindAsync(new FindElementRequest(Kind: FindKind.Interactive, Within: id, ClassName: "Button"));
        var text = await FindAsync(new FindElementRequest(Kind: FindKind.Text, Within: id, AutomationId: "btnSave"));

        Ids(interactive).Should().Equal(["btnSave", "btnSaveAs", "btnApply"], "a Group is not interactive");
        text.Matches.Should().BeEmpty("a Button is not a text kind");
    }

    [Fact]
    public async Task An_offscreen_match_is_dropped_by_default_and_kept_with_include_offscreen()
    {
        var root = Build(N("Root", PaneType, "root", children:
        [
            N("Hidden", ButtonType, "hidden", offscreen: true),
            N("Shown", ButtonType, "shown"),
        ]));
        var id = Issue(root);

        var visible = await FindAsync(new FindElementRequest(Within: id, AutomationId: "hidden"));
        var all = await FindAsync(new FindElementRequest(Within: id, AutomationId: "hidden", IncludeOffscreen: true));

        visible.Matches.Should().BeEmpty();
        visible.Total.Should().Be(0, "visibility is a filter like the others: Total counts what survives it");
        Ids(all).Should().Equal("hidden");
    }

    // ============================================================================================
    // F3: a WINDOW root is a candidate and passes the same filters. RE-PINNED (R2): a within
    // element is not — within searches its descendants only (see the W1 section).
    // ============================================================================================

    [Theory]
    [InlineData("dlg", null, null)]
    [InlineData(null, "#32770", null)]
    [InlineData(null, null, "Pane")]
    public async Task A_window_root_is_a_match_when_it_satisfies_the_filters(string? aid, string? cls, string? type)
    {
        // Was The_root_is_a_match_when_it_satisfies_the_filters, rooted by within; R2 moves the
        // rule to the roots a scope walks (here the desktop's windows, through the seam).
        Desktop(Dialog());

        var result = await FindAsync(new FindElementRequest(Scope: FindScope.Desktop, AutomationId: aid, ClassName: cls, ControlType: type));

        Ids(result).Should().Equal("dlg");
    }

    [Theory]
    [InlineData("dlg", null, null)]
    [InlineData(null, "#32770", null)]
    [InlineData(null, null, "Pane")]
    public async Task The_within_element_is_not_a_match_even_when_it_satisfies_the_filters(string? aid, string? cls, string? type)
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: aid, ClassName: cls, ControlType: type));

        result.Matches.Should().BeEmpty("R2: within searches inside the element, never the element itself");
        result.Total.Should().Be(0);
    }

    [Theory]
    [InlineData("btnApply", null, null, new[] { "btnApply" })]
    [InlineData(null, "Edit", null, new[] { "txtSearch" })]
    [InlineData(null, null, "TreeItem", new[] { "tiNode" })]
    public async Task The_root_is_not_a_match_just_because_it_is_the_root(string? aid, string? cls, string? type, string[] expected)
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: aid, ClassName: cls, ControlType: type));

        Ids(result).Should().Equal(expected);
        result.Total.Should().Be(expected.Length);
    }

    // ============================================================================================
    // F4: empty text with automation_id returns exactly the one unnamed control
    // ============================================================================================

    [Fact]
    public async Task Empty_text_with_an_automation_id_returns_exactly_the_one_unnamed_control()
    {
        var toolbar = Enumerable.Range(0, 30).Select(i => N("", ButtonType, $"tb{i:00}", "ToolbarButton")).ToList();
        toolbar.Insert(17, N("", ButtonType, "tbNew", "ToolbarButton"));
        var root = Build(N("Toolbar", PaneType, "toolbar", "ToolbarWindow32", children: toolbar.ToArray()));

        var result = await FindAsync(new FindElementRequest("", Within: Issue(root), AutomationId: "tbNew"));

        var match = result.Matches.Should().ContainSingle("not the first 20 of everything").Subject;
        match.AutomationId.Should().Be("tbNew");
        match.Name.Should().BeEmpty();
        match.ControlType.Should().Be("Button");
        result.Total.Should().Be(1);
        result.Truncated.Should().BeFalse();
    }

    // ============================================================================================
    // W1 / W3: within searches that element's DESCENDANTS (R2), and replaces scope
    // ============================================================================================

    [Fact]
    public async Task Within_searches_only_the_elements_descendants()
    {
        // RE-PINNED (R2): was Within_searches_only_that_element_and_its_descendants, which
        // expected grpOptions itself first.
        var root = Dialog();
        var group = Child(root, "grpOptions");

        var result = await FindAsync(new FindElementRequest(Within: Issue(group)));

        Ids(result).Should().Equal(["btnApply", "tiNode"], "its descendants; not the element itself, nothing outside it");
        result.Total.Should().Be(2);
    }

    [Fact]
    public async Task A_match_outside_the_within_subtree_is_not_returned()
    {
        var root = Dialog();
        var group = Child(root, "grpOptions");

        var result = await FindAsync(new FindElementRequest("Save", Within: Issue(group)));

        result.Matches.Should().BeEmpty("both Save buttons are in the dialog, outside the group");
        result.Total.Should().Be(0);
    }

    [Fact]
    public async Task The_within_element_is_never_a_match()
    {
        // RE-PINNED (R2): was The_within_element_is_itself_a_candidate_under_the_filters, which
        // expected grpOptions (class Button) before btnApply.
        var root = Dialog();
        var group = Child(root, "grpOptions");

        var result = await FindAsync(new FindElementRequest(Within: Issue(group), ClassName: "Button"));

        Ids(result).Should().Equal("btnApply");
    }

    [Theory]
    [InlineData(FindScope.Window, "Notepad")]
    [InlineData(FindScope.Desktop, null)]
    [InlineData(FindScope.Foreground, "Notepad")]
    public async Task Within_with_a_scope_or_window_title_is_refused_before_any_walk(FindScope scope, string? windowTitle)
    {
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Scope: scope, WindowTitle: windowTitle, Within: id));

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Message.Should().Contain("within", "the refusal names the conflict");
        ex.ParamName.Should().Be("within");
        root.FindCalls.Should().Be(0);
    }

    // ============================================================================================
    // GREEN (coordinator decision 1): an automation id or class name copied from a result finds
    // its control even when the provider's raw value carries characters the sanitiser removed.
    // ============================================================================================

    [Theory]
    [InlineData("btnSave", null)]                // as this server printed it
    [InlineData("btn\u0007Save", null)]          // as the provider reports it
    [InlineData(null, "Button")]
    [InlineData(null, "bu\u0000TTON")]
    public async Task A_filter_matches_the_providers_raw_value_or_the_sanitised_value_a_result_printed(string? aid, string? cls)
    {
        var root = Build(N("Root", PaneType, children:
        [
            N("Target", ButtonType, "btn\u0007Save", "Bu\u0000tton"),
            N("Other", ButtonType, "btnOther", "Other"),
        ]));

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: aid, ClassName: cls));

        result.Matches.Select(m => m.Name).Should().Equal("Target");
    }

    /// <summary>
    /// GREEN bite check: the automation id of a DESCENDANT is compared by the UIA condition the
    /// service pushes down, so a client-side comparison made case-insensitive survived every row
    /// above. The walk's root is judged client-side only — this row pins the comparison there.
    /// </summary>
    [Theory]
    [InlineData("dlg", true)]
    [InlineData("DLG", false)]
    [InlineData("Dlg", false)]
    public async Task The_roots_automation_id_is_compared_case_sensitively(string automationId, bool matches)
    {
        // RE-PINNED (R2): rooted by within before; a within element is no longer a candidate, so
        // the client-side root comparison is now reached through a window root.
        Desktop(Dialog());

        var result = await FindAsync(OnDesktop(automationId: automationId));

        Ids(result).Should().Equal(matches ? ["dlg"] : []);
    }

    // ---- R1: raw-exact first, then the sanitised fallback, per root -----------------------------

    [Fact]
    public async Task When_a_raw_exact_automation_id_exists_a_sanitised_lookalike_is_not_also_returned()
    {
        // X's raw id IS "btnSave"; Y's raw id only SANITISES to it. The exact path finds X, so the
        // fallback is not run for this root and Y is not returned.
        var root = Build(N("Root", PaneType, children:
        [
            N("X", ButtonType, "btnSave"),
            N("Y", ButtonType, "btn\u0007Save"),
        ]));

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: "btnSave"));

        result.Matches.Select(m => m.Name).Should().Equal("X");
        result.Total.Should().Be(1);
    }

    [Theory]
    [InlineData("  btnPad  ")]           // the sanitiser trims
    [InlineData("btn\u0001Pad\u0007")]   // and strips control characters
    public async Task An_automation_id_as_this_server_printed_it_finds_its_control_through_the_fallback(string raw)
    {
        var root = Build(N("Root", PaneType, children: [N("Other", ButtonType, "x"), N("Pad", ButtonType, raw)]));

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: "btnPad"));

        result.Matches.Select(m => m.Name).Should().Equal("Pad");
        result.Matches[0].AutomationId.Should().Be("btnPad", "the id the result prints is the one that was asked for");
    }

    [Fact]
    public async Task The_sanitised_fallback_is_decided_per_root()
    {
        // Window A holds the raw-exact id, window B only a sanitised lookalike: A answers through
        // the exact path, B (where the exact path found nothing) through the fallback.
        var a = Build(N("Win A", PaneType, "winA", children: [N("X", ButtonType, "btnSave")]));
        var b = Build(N("Win B", PaneType, "winB", children: [N("Y", ButtonType, "btn\u0007Save")]));
        Desktop(a, b);

        var result = await FindAsync(OnDesktop(automationId: "btnSave"));

        result.Matches.Select(m => m.Name).Should().Equal("X", "Y");
        result.Total.Should().Be(2);
    }

    [Fact]
    public async Task A_sanitised_automation_id_still_compares_case_sensitively()
    {
        var root = Build(N("Root", PaneType, children: [N("Target", ButtonType, "btn\u0007Save")]));

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: "BTNSAVE"));

        result.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_control_type_is_quoted_clipped_to_eighty_characters()
    {
        var root = Dialog();
        var id = Issue(root);
        var huge = new string('x', 200);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: id, ControlType: huge));

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Message.Should().Contain(new string('x', UiRefusals.MaxQuoted) + "...").And.NotContain(new string('x', UiRefusals.MaxQuoted + 1));
        ex.ParamName.Should().Be("controlType");
    }

    // ---- GREEN round 2 (settled): the fallback's cost and where it is skipped -------------------

    [Theory]
    [InlineData("btnSave", 1)]          // raw-exact match: the exact walk only
    [InlineData("btnPrinted", 2)]       // only a sanitised lookalike: exact walk, then the fallback
    [InlineData("noSuchId", 2)]         // the id does not exist: each root is walked twice (accepted cost)
    [InlineData(null, 1)]               // no automation id: one walk
    public async Task The_within_root_is_walked_once_or_twice_never_more(string? automationId, int walks)
    {
        var root = Build(N("Root", PaneType, children:
        [
            N("Exact", ButtonType, "btnSave"),
            N("Printed", ButtonType, "btn\u0007Printed"),
        ]));

        await FindAsync(new FindElementRequest(Within: Issue(root), AutomationId: automationId));

        root.FindCalls.Should().Be(walks);
    }

    [Fact]
    public async Task A_window_root_that_matches_by_its_sanitised_id_is_that_roots_match_and_the_fallback_is_not_run()
    {
        // The window's own raw id only sanitises to "winA"; so does a child's. The window root is
        // judged client-side (sanitised form allowed) and counts as this root's match, so the
        // fallback walk that would find the child never runs.
        var win = Build(N("Win A", PaneType, "win\u0007A", "Window", children: [N("Child", ButtonType, "win\u0001A")]));
        Desktop(win);

        var result = await FindAsync(OnDesktop(automationId: "winA"));

        result.Matches.Select(m => m.Name).Should().Equal("Win A");
        win.FindCalls.Should().Be(1, "the exact walk only");
    }

    [Fact]
    public async Task The_kind_and_control_type_refusal_names_the_type_the_lower_case_kind_and_every_allowed_type()
    {
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Kind: FindKind.Text, Within: id, ControlType: "button"));

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Message.Should().Contain("Button").And.Contain("text").And.Contain("Text, Edit, Document");
        ex.Message.Should().NotContain("kind Text", "the kind is named the way the caller spells it: lower case");
    }

    // ---- GREEN round 2 (settled): wait mode reports a floor when it stopped at a full page -------
    // The non-counting search is not reachable from any public result (a wait keeps only the
    // matches), so these drive the private FindOnSta through reflection, the way the harness
    // already reaches _cache. Design request: make FindOnSta/FindFilter internal for this.

    private FindElementResult FindNotCounting(FindElementRequest request)
    {
        var filterType = typeof(UIAutomationService).GetNestedType("FindFilter", BindingFlags.NonPublic)!;
        var filter = filterType.GetMethod("From", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [request])!;
        var copy = filterType.GetMethod("<Clone>$")!.Invoke(filter, null)!;
        filterType.GetProperty("Count")!.SetValue(copy, false);
        var findOnSta = typeof(UIAutomationService).GetMethod("FindOnSta", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try { return (FindElementResult)findOnSta.Invoke(_svc, [copy, CancellationToken.None])!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }

    [Fact]
    public void A_non_counting_search_that_stopped_at_a_full_page_reports_a_lower_bound()
    {
        var root = List(30);

        var page = FindNotCounting(new FindElementRequest(Within: Issue(root), ControlType: "ListItem"));

        page.Matches.Should().HaveCount(20);
        page.Total.Should().Be(20, "it did not count past the page");
        page.TotalIsLowerBound.Should().BeTrue();
        page.Truncated.Should().BeTrue();
    }

    [Fact]
    public void A_non_counting_search_whose_page_filled_at_the_very_last_candidate_reports_an_exact_total()
    {
        var root = List(20);

        var page = FindNotCounting(new FindElementRequest(Within: Issue(root), ControlType: "ListItem"));

        page.Matches.Should().HaveCount(20);
        page.Total.Should().Be(20);
        page.TotalIsLowerBound.Should().BeFalse("nothing was left unexamined");
        page.Truncated.Should().BeFalse();
    }

    [Fact]
    public void A_non_counting_search_whose_page_filled_at_the_end_of_a_middle_root_reports_a_lower_bound()
    {
        Desktop(TopWindow("A", 20), TopWindow("B", 3));

        var page = FindNotCounting(OnDesktop("Row"));

        page.Total.Should().Be(20);
        page.TotalIsLowerBound.Should().BeTrue("window B was never looked at");
    }

    // ---- R7: whitespace-only text is no text --------------------------------------------------

    [Theory]
    [InlineData("  ")]
    [InlineData("\t ")]
    public async Task Whitespace_text_with_an_automation_id_finds_the_unnamed_control(string text)
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(text, Within: Issue(root), AutomationId: "tbNew"));

        Ids(result).Should().Equal("tbNew");
    }

    [Fact]
    public async Task Whitespace_text_alone_matches_what_empty_text_matches()
    {
        var root = Dialog();
        var id = Issue(root);

        var blank = await FindAsync(new FindElementRequest("   ", Within: id));
        var empty = await FindAsync(new FindElementRequest("", Within: id));

        Ids(blank).Should().Equal(Ids(empty));
        blank.Total.Should().Be(empty.Total);
    }

    // ---- R6: a kind and a control type that exclude each other are refused ---------------------

    [Theory]
    [InlineData(FindKind.Interactive, "Pane", "Button")]
    [InlineData(FindKind.Interactive, "Text", "Button")]
    [InlineData(FindKind.Text, "Button", "Edit")]
    [InlineData(FindKind.Text, "ListItem", "Document")]
    public async Task A_kind_and_a_control_type_that_exclude_each_other_are_refused_before_any_walk(
        FindKind kind, string controlType, string anAllowedType)
    {
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Kind: kind, Within: id, ControlType: controlType));

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("controlType");
        ex.Message.Should().Contain(controlType, "it names the control type")
            .And.ContainEquivalentOf(kind.ToString(), "and the kind")
            .And.Contain(anAllowedType, "and what the kind allows");
        root.FindCalls.Should().Be(0);
    }

    [Fact]
    public async Task Every_kind_accepts_exactly_the_control_types_it_can_return()
    {
        // interactive: UiClassifier's set; text: Text, Edit, Document; scrollable and any: all.
        var interactive = UIAutomationService.InteractiveControlTypes.Select(t => t.ToString()).ToHashSet();
        var text = new HashSet<string> { "Text", "Edit", "Document" };
        var names = Enum.GetNames<ControlType>().Where(n => n != nameof(ControlType.Unknown)).ToArray();
        var root = Dialog();
        var id = Issue(root);
        var wrong = new List<string>();

        foreach (var kind in new[] { FindKind.Any, FindKind.Interactive, FindKind.Text, FindKind.Scrollable })
            foreach (var name in names)
            {
                bool allowed = kind switch
                {
                    FindKind.Interactive => interactive.Contains(name),
                    FindKind.Text => text.Contains(name),
                    _ => true,
                };
                bool refused;
                try { await _svc.FindElementAsync(new FindElementRequest(Kind: kind, Within: id, ControlType: name)); refused = false; }
                catch (ArgumentException) { refused = true; }
                if (refused == allowed) wrong.Add($"{kind}+{name}: {(refused ? "refused" : "accepted")}");
            }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_text_in_the_request_means_no_text_filter()
    {
        var root = Dialog();

        var result = await FindAsync(new FindElementRequest(Text: null!, Within: Issue(root), AutomationId: "btnApply"));

        Ids(result).Should().Equal("btnApply");
    }

    // ============================================================================================
    // W2: an unknown, expired or dead within
    // ============================================================================================

    [Fact]
    public async Task Within_an_unknown_id_is_the_unknown_id_refusal()
    {
        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: "el_999999"));

        var ex = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("el_999999").And.ContainEquivalentOf("unknown");
        ex.Message.Should().NotContainEquivalentOf("expired");
    }

    [Fact]
    public async Task Within_an_expired_id_is_the_expired_refusal()
    {
        var root = Dialog();
        var id = Issue(root);
        Cache.Remove(id).Should().BeTrue("precondition: the id was issued and is now dropped");

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: id));

        var ex = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which;
        ex.Message.Should().Contain(id).And.ContainEquivalentOf("expired");
        root.FindCalls.Should().Be(0);
    }

    [Fact]
    public async Task Within_an_element_whose_control_was_destroyed_is_Gone_not_an_empty_result()
    {
        // A destroyed Win32 control keeps answering with defaults: ProcessId 0, an empty name, a
        // Pane, no children. "No matches" (or the dead root as its own match) would be a lie about
        // a subtree that is not there.
        var root = Build(N("", PaneType));
        root.ProcessId = () => 0;
        var id = Issue(root);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: id));

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Be(UiRefusals.Gone(id).Message);
    }

    [Fact]
    public async Task Within_an_element_whose_provider_is_gone_is_Gone()
    {
        // The other shape of death: every read and every search raises UIA_E_ELEMENTNOTAVAILABLE.
        var root = Dialog();
        root.ProcessId = () => throw FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE);
        root.FindThrows = FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE);
        var id = Issue(root);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: id));

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Be(UiRefusals.Gone(id).Message);
    }

    // ============================================================================================
    // P1: max_results and offset bounds
    // ============================================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public async Task Max_results_outside_1_to_100_is_refused_stating_the_bounds_before_any_walk(int maxResults)
    {
        var root = List(30);
        var id = Issue(root);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: id, MaxResults: maxResults));

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().MatchRegex("(?i)^max_?results$");
        ex.Message.Should().Contain("1").And.Contain("100").And.Contain(maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture));
        root.FindCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task A_negative_offset_is_refused_before_any_walk(int offset)
    {
        var root = List(30);
        var id = Issue(root);

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: id, Offset: offset));

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().MatchRegex("(?i)^offset$");
        ex.Message.Should().Contain(offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        root.FindCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Max_results_at_either_bound_is_accepted(int maxResults)
    {
        var root = List(120);

        var result = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem", MaxResults: maxResults));

        result.Matches.Should().HaveCount(maxResults);
        result.Total.Should().Be(120);
    }

    // ============================================================================================
    // P2 / P3: pages are consecutive slices of the walk-ordered match list
    // ============================================================================================

    [Fact]
    public async Task Consecutive_offsets_return_consecutive_non_overlapping_pages_in_walk_order()
    {
        var root = List(30);
        var id = Issue(root);

        var first = await FindAsync(new FindElementRequest(Within: id, ControlType: "ListItem", MaxResults: 10, Offset: 0));
        var second = await FindAsync(new FindElementRequest(Within: id, ControlType: "ListItem", MaxResults: 10, Offset: 10));
        var third = await FindAsync(new FindElementRequest(Within: id, ControlType: "ListItem", MaxResults: 10, Offset: 20));

        first.Matches.Select(m => m.Name).Should().Equal(Enumerable.Range(0, 10).Select(i => $"Row {i:00}"));
        second.Matches.Select(m => m.Name).Should().Equal(Enumerable.Range(10, 10).Select(i => $"Row {i:00}"));
        third.Matches.Select(m => m.Name).Should().Equal(Enumerable.Range(20, 10).Select(i => $"Row {i:00}"));
        foreach (var page in new[] { first, second, third })
        {
            page.Total.Should().Be(30);
            page.TotalIsLowerBound.Should().BeFalse("a 31-element tree is far inside the counting budget");
        }
        (first.Offset, second.Offset, third.Offset).Should().Be((0, 10, 20), "Offset echoes the request");
        (first.Truncated, second.Truncated, third.Truncated).Should().Be((true, true, false),
            "Truncated = Total > Offset + Matches.Length");
    }

    [Fact]
    public async Task A_short_last_page_holds_the_remainder_and_is_not_truncated()
    {
        var root = List(30);

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem", MaxResults: 10, Offset: 25));

        page.Matches.Select(m => m.Name).Should().Equal("Row 25", "Row 26", "Row 27", "Row 28", "Row 29");
        page.Total.Should().Be(30);
        page.Offset.Should().Be(25);
        page.Truncated.Should().BeFalse();
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(1000)]
    public async Task An_offset_at_or_past_the_end_is_an_empty_page_with_the_real_Total(int offset)
    {
        var root = List(30);

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem", MaxResults: 10, Offset: offset));

        page.Matches.Should().BeEmpty();
        page.Total.Should().Be(30);
        page.Offset.Should().Be(offset);
        page.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task The_default_request_returns_the_first_twenty_matches_in_walk_order_and_counts_the_rest()
    {
        var root = List(30);

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem"));

        page.Matches.Select(m => m.Name).Should().Equal(Enumerable.Range(0, 20).Select(i => $"Row {i:00}"));
        page.Total.Should().Be(30);
        page.Offset.Should().Be(0);
        page.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_page_that_ends_exactly_at_the_last_match_is_not_truncated()
    {
        // Off-by-one at the cap: twenty matches, a twenty-row page. Truncated is Total > Offset +
        // Matches.Length, which is 20 > 20: false.
        var root = List(20);

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem"));

        page.Matches.Should().HaveCount(20);
        page.Total.Should().Be(20);
        page.Truncated.Should().BeFalse();
        page.TotalIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_token_is_honoured_before_any_walk()
    {
        var root = Dialog();
        var id = Issue(root);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest(Within: id), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        root.FindCalls.Should().Be(0);
    }

    // ============================================================================================
    // P4: ids are minted only for the page
    // ============================================================================================

    [Theory]
    [InlineData(0, 5)]
    [InlineData(20, 5)]
    [InlineData(28, 5)]    // a short page: two
    [InlineData(40, 5)]    // past the end: none
    public async Task Ids_are_minted_only_for_the_elements_on_the_page(int offset, int maxResults)
    {
        var root = List(30);
        var id = Issue(root);
        var before = _svc.CachedElementCount;

        var page = await FindAsync(new FindElementRequest(Within: id, ControlType: "ListItem", MaxResults: maxResults, Offset: offset));

        _svc.CachedElementCount.Should().Be(before + page.Matches.Length,
            "counting the other matches must not put them in the id cache");
        page.Matches.Select(m => m.ElementId).Should().OnlyHaveUniqueItems();
        foreach (var m in page.Matches)
            (await _svc.GetElementAsync(m.ElementId)).Name.Should().Be(m.Name, "every returned id resolves");
    }

    // ============================================================================================
    // P5: counting is bounded
    // ============================================================================================

    [Fact]
    public void The_counting_budget_is_five_thousand()
    {
        UIAutomationService.FindCountBudget.Should().Be(5000);
    }

    [Fact]
    public async Task When_the_counting_budget_runs_out_Total_is_a_lower_bound()
    {
        var root = Build(N("Huge", ListType, "huge", "ListBox"));
        int matching = 20 + UIAutomationService.FindCountBudget + 200;
        AddBulk(root, matching, N("Row", ListItemType, "row", "Row"));

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem"));

        page.Matches.Should().HaveCount(20);
        page.TotalIsLowerBound.Should().BeTrue("the budget ran out before the walk did");
        page.Truncated.Should().BeTrue();
        page.Total.Should().BeGreaterThan(20).And.BeLessThan(matching,
            "a lower bound: more than the page, fewer than really match");
        page.Total.Should().BeLessThanOrEqualTo(20 + UIAutomationService.FindCountBudget + 1,
            "at most FindCountBudget candidates are examined after the page is full");
    }

    [Fact]
    public async Task Inside_the_counting_budget_Total_is_exact()
    {
        var root = Build(N("Big", ListType, "big", "ListBox"));
        int matching = 20 + UIAutomationService.FindCountBudget - 10;
        AddBulk(root, matching, N("Row", ListItemType, "row", "Row"));

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem"));

        page.Matches.Should().HaveCount(20);
        page.Total.Should().Be(matching);
        page.TotalIsLowerBound.Should().BeFalse();
        page.Truncated.Should().BeTrue();
    }

    // GREEN (coordinator decision 2): the budget counts candidates EXAMINED after the page is full;
    // the (budget+1)-th sets TotalIsLowerBound; Truncated is true whenever TotalIsLowerBound is; a
    // page that never fills is never budgeted.

    [Theory]
    [InlineData(0, false)]    // exactly FindCountBudget candidates after the page: all examined, exact
    [InlineData(1, true)]     // one more than the budget: the count stops
    public async Task The_budget_boundary_is_the_budget_plus_first_candidate_after_the_page(int beyondBudget, bool lowerBound)
    {
        var root = Build(N("Big", ListType, "big", "ListBox"));
        int matching = 20 + UIAutomationService.FindCountBudget + beyondBudget;
        AddBulk(root, matching, N("Row", ListItemType, "row", "Row"));

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem"));

        page.TotalIsLowerBound.Should().Be(lowerBound);
        page.Total.Should().Be(20 + UIAutomationService.FindCountBudget,
            lowerBound ? "the candidate past the budget is not counted" : "every match was examined");
    }

    [Fact]
    public async Task A_budget_spent_on_non_matching_candidates_still_marks_the_page_truncated()
    {
        // Twenty matches fill the page, then more than FindCountBudget candidates that do NOT match
        // (class name is matched client-side, so they are all candidates). Total is exactly the
        // page, yet the search could not finish: there may be more, so Truncated is true too.
        var rows = Enumerable.Range(0, 20).Select(i => N($"Row {i:00}", ListItemType, $"row{i:00}", "Wanted")).ToArray();
        var root = Build(N("Mixed", ListType, "mixed", "ListBox", children: rows));
        AddBulk(root, UIAutomationService.FindCountBudget + 1, N("Other", ListItemType, "other", "Unwanted"));

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ClassName: "Wanted"));

        page.Matches.Should().HaveCount(20);
        page.Total.Should().Be(20);
        page.TotalIsLowerBound.Should().BeTrue();
        page.Truncated.Should().BeTrue("a count that stopped early cannot promise there is nothing past the page");
    }

    [Fact]
    public async Task A_page_that_never_fills_is_not_budgeted_and_its_Total_is_exact()
    {
        var root = Build(N("Big", ListType, "big", "ListBox"));
        int matching = UIAutomationService.FindCountBudget + 100;
        AddBulk(root, matching, N("Row", ListItemType, "row", "Row"));

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem", Offset: matching + 1));

        page.Matches.Should().BeEmpty();
        page.Total.Should().Be(matching);
        page.TotalIsLowerBound.Should().BeFalse();
        page.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task The_budget_starts_only_when_a_late_page_fills()
    {
        // Offset 5 000: the page fills at match 5 020; the 80 after it are inside the budget.
        var root = Build(N("Big", ListType, "big", "ListBox"));
        int matching = UIAutomationService.FindCountBudget + 100;
        AddBulk(root, matching, N("Row", ListItemType, "row", "Row"));

        var page = await FindAsync(new FindElementRequest(Within: Issue(root), ControlType: "ListItem", Offset: UIAutomationService.FindCountBudget));

        page.Matches.Should().HaveCount(20);
        page.Total.Should().Be(matching);
        page.TotalIsLowerBound.Should().BeFalse();
        page.Truncated.Should().BeTrue();
    }

    // ============================================================================================
    // R3b / R3c: find_element counts only within the root where the page filled. Several window
    // roots come from the desktop seam (scope=desktop); window roots do not match "Row".
    // ============================================================================================

    [Fact]
    public async Task Once_the_page_fills_a_later_roots_walk_is_not_started_and_Total_is_a_lower_bound()
    {
        var a = TopWindow("A", 6);
        var b = TopWindow("B", 3);
        var c = TopWindow("C", 3);
        Desktop(a, b, c);

        var page = await FindAsync(OnDesktop("Row", maxResults: 5));

        page.Matches.Select(m => m.Name).Should().Equal(Enumerable.Range(0, 5).Select(i => $"A Row {i:00}"));
        b.FindCalls.Should().Be(0, "the page filled in window A: window B's walk is never started");
        c.FindCalls.Should().Be(0);
        page.Total.Should().Be(6, "window A, where the page filled, is counted to the end");
        page.TotalIsLowerBound.Should().BeTrue("windows were left unvisited");
        page.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_page_that_fills_exactly_at_the_end_of_a_middle_root_still_leaves_the_later_roots_unvisited()
    {
        var a = TopWindow("A", 2);
        var b = TopWindow("B", 3);
        var c = TopWindow("C", 4);
        Desktop(a, b, c);

        var page = await FindAsync(OnDesktop("Row", maxResults: 5));

        page.Matches.Should().HaveCount(5);
        c.FindCalls.Should().Be(0);
        page.Total.Should().Be(5);
        page.TotalIsLowerBound.Should().BeTrue("window C was never looked at, so there may be more");
        page.Truncated.Should().BeTrue("Truncated is true whenever TotalIsLowerBound is");
    }

    [Fact]
    public async Task A_page_that_fills_in_the_last_root_has_an_exact_Total()
    {
        var a = TopWindow("A", 2);
        var b = TopWindow("B", 2);
        var c = TopWindow("C", 10);
        Desktop(a, b, c);

        var page = await FindAsync(OnDesktop("Row", maxResults: 5));

        page.Matches.Select(m => m.Name).Should().Equal("A Row 00", "A Row 01", "B Row 00", "B Row 01", "C Row 00");
        page.Total.Should().Be(14);
        page.TotalIsLowerBound.Should().BeFalse();
        page.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_page_that_never_fills_walks_every_root_and_has_an_exact_Total()
    {
        var a = TopWindow("A", 2);
        var b = TopWindow("B", 2);
        var c = TopWindow("C", 2);
        Desktop(a, b, c);

        var page = await FindAsync(OnDesktop("Row"));

        page.Matches.Should().HaveCount(6);
        new[] { a.FindCalls, b.FindCalls, c.FindCalls }.Should().OnlyContain(n => n > 0, "every window is walked");
        page.Total.Should().Be(6);
        page.TotalIsLowerBound.Should().BeFalse();
        page.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_window_whose_search_fails_is_skipped_and_the_next_window_still_answers()
    {
        // D-5 kept through the round-2 walk: a window that cannot be searched (here every Find*
        // raises UIA_E_ELEMENTNOTAVAILABLE: it closed mid-walk) is skipped, not fatal.
        var a = TopWindow("A", 3);
        a.FindThrows = FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE);
        var b = TopWindow("B", 2);
        Desktop(a, b);

        var page = await FindAsync(OnDesktop("Row"));

        page.Matches.Select(m => m.Name).Should().Equal("B Row 00", "B Row 01");
        page.Total.Should().Be(2);
    }

    [Fact]
    public async Task An_offset_past_the_end_walks_every_root_and_reports_the_exact_Total()
    {
        var a = TopWindow("A", 3);
        var b = TopWindow("B", 3);
        Desktop(a, b);

        var page = await FindAsync(OnDesktop("Row", offset: 50));

        page.Matches.Should().BeEmpty();
        page.Total.Should().Be(6);
        page.TotalIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public async Task The_budget_still_applies_inside_the_root_where_the_page_filled()
    {
        var a = Build(N("Win A", PaneType, "winA", "Window"));
        AddBulk(a, 20 + UIAutomationService.FindCountBudget + 50, N("A Row", ListItemType, "row", "Row"));
        var b = TopWindow("B", 3);
        Desktop(a, b);

        var page = await FindAsync(OnDesktop("Row"));

        page.Matches.Should().HaveCount(20);
        page.Total.Should().Be(20 + UIAutomationService.FindCountBudget);
        page.TotalIsLowerBound.Should().BeTrue();
        b.FindCalls.Should().Be(0);
    }

    // ============================================================================================
    // R4: the walk observes the token, before each root and before each candidate
    // ============================================================================================

    [Fact]
    public async Task A_token_cancelled_mid_walk_stops_the_walk_within_one_candidate()
    {
        var root = Build(N("Big", ListType, "big", "ListBox"));
        var row = N("Row", ListItemType, "row", "Row");
        AddBulk(root, 500, row);
        var id = Issue(root);
        using var cts = new CancellationTokenSource();
        int readsAtCancel = -1;
        _onNameRead = n =>
        {
            if (n == row && n.NameReads == 10) { readsAtCancel = n.NameReads; cts.Cancel(); }
        };

        // "zzz" matches nothing, so every candidate costs exactly one Name read and none is described.
        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest("zzz", Within: id), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _onNameRead = null;
        await _svc.GetElementAsync(id);   // queued behind the walk on the one STA thread: the walk is over
        readsAtCancel.Should().Be(10, "precondition: the token was cancelled mid-walk");
        (row.NameReads - readsAtCancel).Should().BeLessThanOrEqualTo(1,
            "the walk checks the token before each candidate; it does not run on to the end of the 500");
    }

    [Fact]
    public async Task A_token_cancelled_in_one_root_does_not_start_the_next()
    {
        var a = Build(N("Win A", PaneType, "winA", "Window"));
        var row = N("A Row", ListItemType, "row", "Row");
        AddBulk(a, 30, row);
        var b = TopWindow("B", 3);
        Desktop(a, b);
        using var cts = new CancellationTokenSource();
        _onNameRead = n => { if (n == row && n.NameReads == 30) cts.Cancel(); };   // A's last candidate

        Func<Task> act = () => _svc.FindElementAsync(OnDesktop("zzz"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _onNameRead = null;
        await _svc.GetElementAsync(Issue(b));   // the walk is over
        b.FindCalls.Should().Be(0, "the token is checked before each root");
    }

    [Fact]
    public async Task A_token_cancelled_in_one_root_does_not_start_the_next_even_when_that_root_is_not_a_candidate()
    {
        // GREEN round 2 bite check: in the row above, window B's root is a candidate, so the
        // per-candidate check stops the walk at B's root and the per-ROOT check was never needed —
        // removing it survived. With kind:interactive a Pane window root is not a candidate, so
        // only the check before each root keeps B's descendant walk from starting.
        var a = Build(N("Win A", PaneType, "winA", "Window"));
        var row = N("A Row", ListItemType, "row", "Row");
        AddBulk(a, 30, row);
        var b = TopWindow("B", 3);
        Desktop(a, b);
        using var cts = new CancellationTokenSource();
        _onNameRead = n => { if (n == row && n.NameReads == 30) cts.Cancel(); };   // A's last candidate

        Func<Task> act = () => _svc.FindElementAsync(new FindElementRequest("zzz", FindKind.Interactive, FindScope.Desktop), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _onNameRead = null;
        await _svc.GetElementAsync(Issue(b));   // the walk is over
        b.FindCalls.Should().Be(0, "the token is checked before each root");
    }

    // ============================================================================================
    // V1 / V2: wait_for's element conditions take the same filters
    // ============================================================================================

    /// <summary>
    /// The wait searched inside the fake <c>within</c> element. Without this a wait that ignores
    /// <c>Within</c> walks the REAL foreground window, and its verdict depends on whatever app is in
    /// front (a browser's "Saved Tab Groups" satisfies a wait for "Save"): the row would go red or
    /// green for a reason that has nothing to do with E-4.
    /// </summary>
    private void SearchedInside(Node within) =>
        within.FindCalls.Should().BeGreaterThan(0, "the wait's find was rooted at the within element, not the live desktop");

    [Fact]
    public async Task Element_exists_with_an_automation_id_waits_for_that_control_only()
    {
        var root = Dialog();

        var result = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 0,
            Within: Issue(root), AutomationId: "btnSaveAs"));

        _harnessErrors.Should().BeEmpty();
        SearchedInside(root);
        result.Satisfied.Should().BeTrue(result.Detail);
        result.Element!.AutomationId.Should().Be("btnSaveAs", "the first 'Save' is btnSave; the filter picks the second");
    }

    [Fact]
    public async Task Element_enabled_with_an_automation_id_judges_that_control_only()
    {
        // btnSave is enabled, btnSaveAs disabled: without the filter the wait is satisfied by btnSave.
        var root = Dialog(saveAsEnabled: false);

        var result = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementEnabled, "Save", TimeoutMs: 0,
            Within: Issue(root), AutomationId: "btnSaveAs"));

        _harnessErrors.Should().BeEmpty();
        SearchedInside(root);
        result.Satisfied.Should().BeFalse();
        result.Detail.Should().Contain("disabled");
        result.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData("Search", "Edit", null, null, true)]
    [InlineData("Search", "Button", null, null, false)]
    [InlineData("Save", null, "Edit", null, false)]
    [InlineData("Save", null, "button", null, true)]
    [InlineData("Apply", null, null, "grpOptions", true)]
    [InlineData("Save", null, null, "grpOptions", false)]
    public async Task Element_exists_honours_control_type_class_name_and_within(
        string text, string? controlType, string? className, string? withinAid, bool satisfied)
    {
        var root = Dialog();
        var withinNode = withinAid is null ? root : Child(root, withinAid);
        var within = Issue(withinNode);

        var result = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, text, TimeoutMs: 0,
            Within: within, ControlType: controlType, ClassName: className));

        _harnessErrors.Should().BeEmpty();
        SearchedInside(withinNode);
        result.Satisfied.Should().Be(satisfied, result.Detail);
        if (!satisfied) result.Detail.Should().Be($"no element matching '{text}'", "a clean poll that found nothing");
    }

    [Fact]
    public async Task Wait_with_an_unknown_within_id_is_refused_before_the_first_poll()
    {
        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 0, Within: "el_999999"));

        var ex = (await act.Should().ThrowAsync<KeyNotFoundException>(
            "a refusal, not a result whose detail says every poll failed")).Which;
        ex.Message.Should().Contain("el_999999").And.ContainEquivalentOf("unknown");
    }

    [Fact]
    public async Task Wait_with_an_expired_within_id_is_refused_before_the_first_poll()
    {
        var root = Dialog();
        var id = Issue(root);
        Cache.Remove(id);

        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementEnabled, "Save", TimeoutMs: 0, Within: id));

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().ContainEquivalentOf("expired");
        root.FindCalls.Should().Be(0);
    }

    [Fact]
    public async Task Wait_with_an_unknown_control_type_is_refused_before_the_first_poll()
    {
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 0,
            Within: id, ControlType: "Buton"));

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Message.Should().Contain("Buton");
        root.FindCalls.Should().Be(0);
    }

    [Fact]
    public async Task Wait_with_within_and_a_scope_is_refused_before_the_first_poll()
    {
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 0,
            Scope: FindScope.Desktop, Within: id));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("within");
        root.FindCalls.Should().Be(0);
    }

    // ---- GREEN (coordinator decision 5): a property filter lifts the text requirement ----------

    [Theory]
    [InlineData(WaitCondition.ElementExists, "tbNew", null, null, "tbNew")]
    [InlineData(WaitCondition.ElementExists, null, "Edit", null, "txtSearch")]
    [InlineData(WaitCondition.ElementExists, null, null, "SysTreeView32Item", "tiNode")]
    [InlineData(WaitCondition.ElementEnabled, "tbNew", null, null, "tbNew")]
    public async Task An_element_wait_with_a_property_filter_needs_no_text(
        WaitCondition condition, string? aid, string? type, string? cls, string expected)
    {
        var root = Dialog();

        var result = await _svc.WaitForAsync(new WaitRequest(condition, "", TimeoutMs: 0,
            Within: Issue(root), AutomationId: aid, ControlType: type, ClassName: cls));

        _harnessErrors.Should().BeEmpty();
        SearchedInside(root);
        result.Satisfied.Should().BeTrue(result.Detail);
        result.Element!.AutomationId.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task An_element_wait_with_only_within_still_needs_text(string blank)
    {
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, blank, TimeoutMs: 0, Within: id));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("needs text");
        root.FindCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(WaitCondition.TextExists)]
    [InlineData(WaitCondition.FocusedElement)]
    [InlineData(WaitCondition.ActiveWindow)]
    public async Task A_property_filter_does_not_lift_the_text_requirement_of_a_condition_that_does_not_search_elements(WaitCondition condition)
    {
        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(condition, "", TimeoutMs: 0, AutomationId: "btnSave"));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("needs text");
    }

    // ---- R5 (reverses decision 4): a within already dead at the start is refused with Gone ----

    [Theory]
    [InlineData(WaitCondition.ElementExists, false)]
    [InlineData(WaitCondition.ElementEnabled, false)]
    [InlineData(WaitCondition.ElementExists, true)]
    public async Task A_wait_within_a_destroyed_container_is_refused_with_Gone_before_the_first_poll(WaitCondition condition, bool providerGone)
    {
        // RE-PINNED (R5): was A_wait_within_a_container_already_dead_is_not_refused_and_every_poll_fails,
        // which pinned Detail "every poll failed: <Gone>". Both shapes of death: a destroyed Win32
        // control (pid 0, defaults) and a provider that cannot answer at all.
        var root = Build(N("", PaneType));
        if (providerGone)
        {
            root.ProcessId = () => throw FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE);
            root.FindThrows = FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE);
        }
        else root.ProcessId = () => 0;
        var id = Issue(root);

        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(condition, "Save", TimeoutMs: 0, Within: id));

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>(
            "the same answer find_element gives, not a result after the whole timeout")).Which;
        ex.Message.Should().Be(UiRefusals.Gone(id).Message);
        root.FindCalls.Should().Be(0, "no find, no poll");
    }

    // ---- R2 in wait_for: the container never satisfies a wait inside itself --------------------

    [Theory]
    [InlineData(WaitCondition.ElementExists)]
    [InlineData(WaitCondition.ElementEnabled)]
    public async Task A_wait_for_the_containers_own_name_within_it_is_not_satisfied_by_the_container(WaitCondition condition)
    {
        var root = Dialog();
        var group = Child(root, "grpOptions");

        var result = await _svc.WaitForAsync(new WaitRequest(condition, "Options", TimeoutMs: 0, Within: Issue(group)));

        _harnessErrors.Should().BeEmpty();
        SearchedInside(group);
        result.Satisfied.Should().BeFalse("the group is named Options, but within searches its descendants only");
        result.Detail.Should().Be("no element matching 'Options'");
    }

    // ---- R3a: a wait does not count -------------------------------------------------------------

    [Fact]
    public async Task An_element_wait_stops_examining_candidates_once_its_page_is_full()
    {
        // Twenty matching rows fill the wait's page; 500 more (one shared fake) follow. find_element
        // would read every one of them to count Total; a wait needs no Total and must not.
        var root = Build(N("Big", ListType, "big", "ListBox",
            children: Enumerable.Range(0, 20).Select(i => N($"Row {i:00}", ListItemType, $"row{i:00}", "Row")).ToArray()));
        var extra = N("Row extra", ListItemType, "extra", "Row");
        AddBulk(root, 500, extra);

        var result = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Row", TimeoutMs: 0, Within: Issue(root)));

        _harnessErrors.Should().BeEmpty();
        result.Satisfied.Should().BeTrue(result.Detail);
        result.Element!.Name.Should().Be("Row 00");
        extra.NameReads.Should().Be(0, "nothing past the full page is examined");
    }

    [Fact]
    public async Task An_element_wait_does_not_start_another_root_once_its_page_is_full()
    {
        var a = TopWindow("A", 25);
        var b = TopWindow("B", 3);
        Desktop(a, b);

        var result = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Row", TimeoutMs: 0, Scope: FindScope.Desktop));

        _harnessErrors.Should().BeEmpty();
        result.Satisfied.Should().BeTrue(result.Detail);
        b.FindCalls.Should().Be(0);
        a.Children.Skip(20).Should().OnlyContain(n => n.NameReads == 0, "rows 20-24 of window A are past the full page");
    }

    // ---- R6 in wait_for -------------------------------------------------------------------------

    [Fact]
    public async Task A_wait_whose_kind_excludes_its_control_type_is_refused_before_the_first_poll()
    {
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 0,
            Kind: FindKind.Interactive, Within: id, ControlType: "Pane"));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("controlType");
        root.FindCalls.Should().Be(0);
    }

    // ---- R7 in wait_for: whitespace text is no text ---------------------------------------------

    [Fact]
    public async Task An_element_wait_with_whitespace_text_and_an_automation_id_finds_the_control()
    {
        var root = Dialog();

        var result = await _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "   ", TimeoutMs: 0,
            Within: Issue(root), AutomationId: "tbNew"));

        _harnessErrors.Should().BeEmpty();
        result.Satisfied.Should().BeTrue(result.Detail);
        result.Element!.AutomationId.Should().Be("tbNew");
    }

    [Fact]
    public async Task A_wait_with_within_and_a_window_title_is_refused_before_the_first_poll()
    {
        // WaitRequest carries no paging (decision 8: always the default page), so its call-level
        // refusals are the filters and the target: within beside a window title is one.
        var root = Dialog();
        var id = Issue(root);

        Func<Task> act = () => _svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 0,
            WindowTitle: "Notepad", Within: id));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("within");
        root.FindCalls.Should().Be(0);
    }
}

/// <summary>
/// E-4 V3 (CHARACTERISATION, not a rule): what the EXISTING poll-failure semantics of
/// <c>WaitLoopAsync</c> yield when the <c>within</c> element dies during a wait. Once E-4 lands,
/// a find inside a dead <c>within</c> throws <see cref="UiRefusals.Gone"/> (W2), and the wait loop
/// treats any throwing poll as a transient failure (D-5). These rows feed exactly that throw to the
/// loop, so they describe today's answer; they are green before E-4 and say nothing about whether
/// that answer is the right one — that decision is the caller's.
/// </summary>
[Trait("Category", "Unit")]
public class WaitForWithinDiesCharacterisationTests
{
    private static WaitRequest Request(WaitCondition condition) =>
        new(condition, "Save", TimeoutMs: 150, IntervalMs: 10, Within: "el_7");

    [Theory]
    [InlineData(WaitCondition.ElementExists)]
    [InlineData(WaitCondition.ElementEnabled)]
    public async Task Within_dying_after_a_clean_poll_reads_as_not_found_and_the_death_is_not_reported(WaitCondition condition)
    {
        var polls = 0;

        var result = await UIAutomationService.WaitLoopAsync(Request(condition), _ =>
        {
            polls++;
            if (polls == 1) return Task.FromResult(new WaitEvidence(Matches: []));
            throw UiRefusals.Gone("el_7");
        }, CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Detail.Should().Be("no element matching 'Save'",
            "the last CLEAN poll's verdict wins; the later Gone failures are retried and then forgotten");
        result.Detail.Should().NotContain("no longer available");
        result.Attempts.Should().BeGreaterThan(1, "the wait keeps polling a dead subtree until the timeout");
    }

    [Theory]
    [InlineData(WaitCondition.ElementExists)]
    [InlineData(WaitCondition.ElementEnabled)]
    public async Task Within_dead_on_every_poll_reads_as_every_poll_failed_naming_the_gone_refusal(WaitCondition condition)
    {
        var result = await UIAutomationService.WaitLoopAsync(Request(condition),
            _ => throw UiRefusals.Gone("el_7"), CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Detail.Should().StartWith("every poll failed: Element 'el_7' is no longer available");
    }
}
