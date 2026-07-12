using FluentAssertions;
using WindowsMcp;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// The MCP SDK masks every non-McpException as "An error occurred invoking '&lt;tool&gt;'.".
/// That hid the PID-reuse guard's abort behind the same text as a crash — so a caller could not
/// tell "I just saved you from killing an innocent process" from "the tool broke", and might
/// retry the kill without the guard. The CallTool filter surfaces caller-facing refusals; this
/// pins down which exceptions qualify, and — just as importantly — which must stay masked.
/// </summary>
[Trait("Category", "Unit")]
public class ToolErrorsTests
{
    [Fact]
    public void Deliberate_refusals_and_bad_input_are_caller_facing()
    {
        // Tools throw these to refuse: missing confirm, bad param combos, unknown action.
        ToolErrors.IsCallerFacing(new ArgumentException("'confirm: true' is required for kill"))
            .Should().BeTrue();

        // The PID-reuse start-time guard aborts with this. Its message IS the point.
        ToolErrors.IsCallerFacing(new InvalidOperationException(
            "pid 30872 start time … != expected …; aborting (possible PID reuse)"))
            .Should().BeTrue();

        // Process.GetProcessById on a dead PID — also actionable for the caller.
        ToolErrors.IsCallerFacing(new ArgumentException("Process with an Id of 999999 is not running."))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(OutOfMemoryException))]
    public void Unexpected_faults_stay_masked(Type faultType)
    {
        // These are OUR bugs, not the caller's. Surfacing them would leak internals for no benefit
        // — the SDK's generic message is the right answer, so the filter must not claim them.
        var ex = (Exception)Activator.CreateInstance(faultType)!;
        ToolErrors.IsCallerFacing(ex).Should().BeFalse();
    }
}
