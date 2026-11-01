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

        ToolErrors.IsCallerFacing(new KeyNotFoundException("Registry path not found"))
            .Should().BeTrue();
        ToolErrors.IsCallerFacing(new DirectoryNotFoundException("Watch path not found"))
            .Should().BeTrue();
        ToolErrors.IsCallerFacing(new NotSupportedException("middle-button drag not supported"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    public void Unexpected_faults_stay_masked(Type faultType)
    {
        // These are OUR bugs, not the caller's. Surfacing them would leak internals for no benefit
        // — the SDK's generic message is the right answer, so the filter must not claim them.
        // C-1 R4-7 adds the last two: a COM HRESULT or a Win32 error code is an internal fault
        // whatever it says, and widening the filter to IOException must not drag them in.
        var ex = (Exception)Activator.CreateInstance(faultType)!;
        ToolErrors.IsCallerFacing(ex).Should().BeFalse();
    }

    // ---- C-1 R4-7: the four types whose messages ARE the answer -------------------------------

    /// <summary>
    /// R4-7: these are the deliberate answers of registry_get ("Registry path not found: …"), the
    /// window matcher ("No top-level window matching 'x'. Open windows: …"), element ids, the app
    /// catalog, scheduled_task, watch, wait_for and every file tool. Masked as "An error occurred
    /// invoking '&lt;tool&gt;'", each of them turns a precise, actionable refusal into a crash
    /// report — and a caller that cannot read "no such window" retries the same call.
    /// </summary>
    [Theory]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(PathTooLongException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(TimeoutException))]
    public void Deliberate_answers_that_arrive_as_system_exceptions_are_caller_facing(Type type)
    {
        var ex = (Exception)Activator.CreateInstance(type)!;

        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
    }

    [Fact]
    public void The_messages_the_widening_exists_for_reach_the_caller()
    {
        // registry_get on a key that is not there.
        ToolErrors.IsCallerFacing(new KeyNotFoundException(
                @"Registry path not found: HKCU\Software\WindowsMcpTests\does-not-exist"))
            .Should().BeTrue();

        // The window matcher listing what IS open is the caller's next move.
        ToolErrors.IsCallerFacing(new KeyNotFoundException(
                "No top-level window matching 'nope'. Open windows: Notepad, Explorer."))
            .Should().BeTrue();

        // file_write(create_parents:false) naming the flag that would have created the directory.
        ToolErrors.IsCallerFacing(new DirectoryNotFoundException(
                @"Directory 'C:\tmp\nope' does not exist; pass create_parents:true to create it"))
            .Should().BeTrue();

        // A file held open by another process: "in use" is something the caller can act on.
        ToolErrors.IsCallerFacing(new IOException(
                "The process cannot access the file because it is being used by another process."))
            .Should().BeTrue();
    }

    // ---- C-1 R4b-6: the message the client actually receives ----------------------------------

    /// <summary>
    /// R4b-6: capping is the last step, not a rewrite. Everything a caller could read has to
    /// survive word for word — 2 000 characters is a screenful and far more than any deliberate
    /// refusal in this server needs.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(2000)]
    public void MessageFor_passes_a_message_within_the_cap_through_unchanged(int length)
    {
        var message = new string('m', length);

        ToolErrors.MessageFor(new InvalidOperationException(message)).Should().Be(message);
    }

    [Fact]
    public void MessageFor_keeps_a_real_refusal_word_for_word()
    {
        const string refusal = @"'C:\Windows' is a volume root and cannot be deleted";

        ToolErrors.MessageFor(new InvalidOperationException(refusal)).Should().Be(refusal);
    }

    /// <summary>
    /// R4b-6: a refusal quotes what the caller sent, and what the caller sent can be enormous — a
    /// registry path they pasted, a path a link cycle grew to 32 KB. Sending it all back is not an
    /// answer: it is the same thing again, at a size that costs the caller their context window.
    /// The head of the message says what happened; a marker says the rest was cut.
    /// </summary>
    [Theory]
    [InlineData(2001)]
    [InlineData(32_000)]
    public void MessageFor_caps_a_long_message_and_marks_it_as_cut(int length)
    {
        var message = @"Registry path not found: HKCU\" + new string('k', length);

        var capped = ToolErrors.MessageFor(new KeyNotFoundException(message));

        capped.Length.Should().BeLessThanOrEqualTo(2000, "no answer to a client is 32 KB of one path");
        capped.Should().StartWith(@"Registry path not found: HKCU\",
            "the head of the message is the part that says what happened");
        (capped.EndsWith('…') || capped.EndsWith("...", StringComparison.Ordinal)).Should().BeTrue(
            "a message cut without saying so reads as the whole answer - the caller acts on a path that is not the one they sent");
    }

    // ---- C-1 R4c-9: the cut is a cut, not a corruption ----------------------------------------

    /// <summary>
    /// R4c-9: the cut lands wherever 2 000 characters land, and what is there is the caller's own
    /// text — a file name, a registry value, a path they pasted. An emoji is ONE character and TWO
    /// <see cref="char"/>s (a surrogate pair), so a cut by index can land between the halves and
    /// leave a lone surrogate. That is not a shortened message: it is an invalid string, which the
    /// JSON writer either rejects or turns into U+FFFD — the caller loses the whole answer to a
    /// message the server chose to send them.
    /// <para>
    /// The head here is 1 972 ASCII characters, which puts the pair astride the cut for the
    /// 27-character marker in force today; the walk below does not depend on that arithmetic.
    /// </para>
    /// </summary>
    [Fact]
    public void MessageFor_never_cuts_a_surrogate_pair_in_half()
    {
        var head = new string('a', 1972);
        var message = head + string.Concat(Enumerable.Repeat("\U0001F600", 514));   // 1972 + 1028 = 3000 chars
        message.Length.Should().Be(3000, "the fixture only means anything if it is over the cap");

        var capped = ToolErrors.MessageFor(new InvalidOperationException(message));

        capped.Length.Should().BeLessThanOrEqualTo(2000, "the cap still applies");
        for (var i = 0; i < capped.Length; i++)
        {
            if (char.IsHighSurrogate(capped[i]))
                (i + 1 < capped.Length && char.IsLowSurrogate(capped[i + 1])).Should().BeTrue(
                    $"the high surrogate at index {i} has to keep its low half - a lone one is not a character");
            else if (char.IsLowSurrogate(capped[i]))
                (i > 0 && char.IsHighSurrogate(capped[i - 1])).Should().BeTrue(
                    $"the low surrogate at index {i} has to keep its high half");
        }
        capped.Should().StartWith(head[..200], "the head of the message is still word for word");
    }

    /// <summary>
    /// R4c-9: "…" alone says something was cut but not what the rule is. A caller who is told the
    /// limit is 2 000 characters can page, hash or narrow what they asked for; one who is not
    /// re-sends the same call.
    /// </summary>
    [Fact]
    public void MessageFor_marks_the_cut_with_the_limit_it_applied()
    {
        var capped = ToolErrors.MessageFor(new InvalidOperationException(new string('m', 5000)));

        var tail = capped[^40..];
        tail.Should().ContainEquivalentOf("cut", "the marker says the message was cut");
        tail.Should().Contain("2000", "and what the limit that cut it is");
    }

    /// <summary>
    /// R4c-9, the case the fixed-length sibling above cannot reach. Where the cut lands depends on
    /// the marker's own length, which is an implementation detail: with today's 30-character
    /// marker the cut falls at index 1 970, so a message whose emoji start at 1 972 is severed in
    /// the ASCII head and the surrogate guard never runs — the test passes whether the guard is
    /// there or not. Sweeping the head length across the whole plausible range puts the pair
    /// astride the cut for SOME case whatever the marker becomes, so the guard is exercised by
    /// construction rather than by luck.
    /// </summary>
    [Theory]
    [InlineData(1950)]
    [InlineData(1960)]
    [InlineData(1965)]
    [InlineData(1966)]
    [InlineData(1967)]
    [InlineData(1968)]
    [InlineData(1969)]
    [InlineData(1970)]
    [InlineData(1971)]
    [InlineData(1972)]
    [InlineData(1973)]
    [InlineData(1974)]
    [InlineData(1975)]
    [InlineData(1980)]
    [InlineData(1990)]
    [InlineData(1999)]
    public void MessageFor_never_cuts_a_surrogate_pair_wherever_the_cut_lands(int headLength)
    {
        var head = new string('a', headLength);
        // 300 emoji: 600 chars, so the message is over the cap for every head length here and the
        // cut is guaranteed to land inside the run of surrogate pairs for at least one of them.
        var message = head + string.Concat(Enumerable.Repeat("\U0001F600", 300));

        var capped = ToolErrors.MessageFor(new InvalidOperationException(message));

        capped.Length.Should().BeLessThanOrEqualTo(2000, "the cap still applies");
        for (var i = 0; i < capped.Length; i++)
        {
            if (char.IsHighSurrogate(capped[i]))
                (i + 1 < capped.Length && char.IsLowSurrogate(capped[i + 1])).Should().BeTrue(
                    $"the high surrogate at index {i} has to keep its low half - a lone one is not a character, "
                    + $"and the JSON writer turns it into U+FFFD or refuses the whole response (head {headLength})");
            else if (char.IsLowSurrogate(capped[i]))
                (i > 0 && char.IsHighSurrogate(capped[i - 1])).Should().BeTrue(
                    $"the low surrogate at index {i} has to keep its high half (head {headLength})");
        }
        capped.Should().StartWith(head[..200], "the head of the message is still word for word");
        capped.Should().ContainEquivalentOf("cut", "and the marker still says the message was cut");
    }
}
