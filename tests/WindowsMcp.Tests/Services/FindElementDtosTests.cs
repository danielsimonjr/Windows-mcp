using System.Text.Json;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-4 D1-D4: the DTO shapes <c>find_element</c> and <c>wait_for</c> carry. These pin the wire
/// format — what an existing client keeps receiving byte for byte, and what the new fields look
/// like — independently of the service that fills them.
/// </summary>
[Trait("Category", "Unit")]
public class FindElementDtosTests
{
    private static ElementInfo Save(string? automationId = null, string? className = null) =>
        new("el_1", "Save", "Button", true, false, new Bounds(1, 2, 3, 4), null, null, null,
            AutomationId: automationId, ClassName: className);

    // ---- D1: ElementInfo ---------------------------------------------------------------------

    [Fact]
    public void ElementInfo_without_the_new_fields_serialises_byte_identically_to_before_E4()
    {
        var json = JsonSerializer.Serialize(Save());

        json.Should().Be(
            "{\"ElementId\":\"el_1\",\"Name\":\"Save\",\"ControlType\":\"Button\",\"IsEnabled\":true,\"IsOffscreen\":false," +
            "\"Bounds\":{\"X\":1,\"Y\":2,\"Width\":3,\"Height\":4},\"Value\":null,\"IsChecked\":null,\"IsSelected\":null,\"Scroll\":null}");
    }

    [Fact]
    public void ElementInfo_carries_AutomationId_and_ClassName_after_the_existing_fields()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Save("btnSave", "Button")));

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "ElementId", "Name", "ControlType", "IsEnabled", "IsOffscreen", "Bounds", "Value", "IsChecked",
            "IsSelected", "Scroll", "AutomationId", "ClassName");
        doc.RootElement.GetProperty("AutomationId").GetString().Should().Be("btnSave");
        doc.RootElement.GetProperty("ClassName").GetString().Should().Be("Button");
    }

    [Theory]
    [InlineData("btnSave", null, "AutomationId", "ClassName")]
    [InlineData(null, "Button", "ClassName", "AutomationId")]
    public void ElementInfo_omits_whichever_of_the_two_is_null(string? automationId, string? className, string present, string absent)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Save(automationId, className)));

        doc.RootElement.TryGetProperty(present, out _).Should().BeTrue();
        doc.RootElement.TryGetProperty(absent, out _).Should().BeFalse();
    }

    // ---- D2: FindElementRequest --------------------------------------------------------------

    [Fact]
    public void FindElementRequest_defaults_are_todays_find()
    {
        var r = new FindElementRequest();

        r.Text.Should().Be("");
        r.Kind.Should().Be(FindKind.Any);
        r.Scope.Should().Be(FindScope.Foreground);
        r.WindowTitle.Should().BeNull();
        r.IncludeOffscreen.Should().BeFalse();
        r.AutomationId.Should().BeNull();
        r.ControlType.Should().BeNull();
        r.ClassName.Should().BeNull();
        r.Within.Should().BeNull();
        r.MaxResults.Should().Be(20);
        r.Offset.Should().Be(0);
    }

    // ---- D3: FindElementResult ---------------------------------------------------------------

    [Fact]
    public void FindElementResult_from_matches_alone_is_one_untruncated_page_of_everything()
    {
        // Every existing `new FindElementResult(matches)` (mocks included) keeps compiling and means
        // "this is the whole result".
        var r = new FindElementResult([Save(), Save()]);

        r.Total.Should().Be(2);
        r.Offset.Should().Be(0);
        r.Truncated.Should().BeFalse();
        r.TotalIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public void FindElementResult_json_is_Matches_Total_Offset_Truncated_and_omits_a_false_lower_bound()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new FindElementResult([Save()], Total: 41, Offset: 20, Truncated: true)));

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("Matches", "Total", "Offset", "Truncated");
        doc.RootElement.GetProperty("Total").GetInt32().Should().Be(41);
        doc.RootElement.GetProperty("Offset").GetInt32().Should().Be(20);
        doc.RootElement.GetProperty("Truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void FindElementResult_json_carries_TotalIsLowerBound_when_true()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            new FindElementResult([Save()], Total: 5021, Offset: 0, Truncated: true, TotalIsLowerBound: true)));

        doc.RootElement.GetProperty("TotalIsLowerBound").GetBoolean().Should().BeTrue();
    }

    // ---- R8: every E-4 DTO round-trips through System.Text.Json -------------------------------

    [Fact]
    public void FindElementResult_round_trips_through_System_Text_Json()
    {
        // Two public constructors and no [JsonConstructor]: Deserialize threw NotSupportedException.
        var page = new FindElementResult([Save("btnSave", "Button"), Save()], Total: 41, Offset: 20, Truncated: true, TotalIsLowerBound: true);

        var back = JsonSerializer.Deserialize<FindElementResult>(JsonSerializer.Serialize(page));

        back.Should().BeEquivalentTo(page, "Matches is an array, so record equality would compare references");
    }

    [Fact]
    public void FindElementResult_with_a_false_lower_bound_round_trips_with_it_false()
    {
        var page = new FindElementResult([Save()], Total: 1);

        var back = JsonSerializer.Deserialize<FindElementResult>(JsonSerializer.Serialize(page))!;

        back.Should().BeEquivalentTo(page);
        back.TotalIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public void FindElementRequest_round_trips_through_System_Text_Json()
    {
        var request = new FindElementRequest("Save", FindKind.Interactive, FindScope.Foreground, null, true,
            "btnSave", "Button", "Button", "el_3", 5, 10);

        JsonSerializer.Deserialize<FindElementRequest>(JsonSerializer.Serialize(request)).Should().Be(request);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("btnSave", "Button")]
    public void ElementInfo_round_trips_through_System_Text_Json(string? automationId, string? className)
    {
        var info = Save(automationId, className);

        JsonSerializer.Deserialize<ElementInfo>(JsonSerializer.Serialize(info)).Should().Be(info);
    }

    [Fact]
    public void WaitRequest_round_trips_through_System_Text_Json()
    {
        var request = new WaitRequest(WaitCondition.ElementEnabled, "Save", 1000, 100, FindKind.Any, FindScope.Foreground,
            null, true, false, AutomationId: "btnSave", ControlType: "Button", ClassName: "Button", Within: "el_3");

        JsonSerializer.Deserialize<WaitRequest>(JsonSerializer.Serialize(request)).Should().Be(request);
    }

    // ---- D4: WaitRequest ---------------------------------------------------------------------

    [Fact]
    public void WaitRequest_find_filters_default_to_none_and_follow_the_existing_parameters()
    {
        // The nine pre-E-4 positional arguments still bind; the four filters are trailing and null.
        var r = new WaitRequest(WaitCondition.ElementExists, "Save", 1000, 100, FindKind.Any, FindScope.Foreground, null, false, false);

        r.AutomationId.Should().BeNull();
        r.ControlType.Should().BeNull();
        r.ClassName.Should().BeNull();
        r.Within.Should().BeNull();

        var filtered = r with { AutomationId = "a", ControlType = "Button", ClassName = "c", Within = "el_3" };
        (filtered.AutomationId, filtered.ControlType, filtered.ClassName, filtered.Within).Should().Be(("a", "Button", "c", "el_3"));
    }
}
