using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using Microsoft.Win32.TaskScheduler;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-10 R13: <c>scheduled_task(action:"create", trigger:"daily")</c>. The service did
/// <c>DateTime.Parse(trigger, Invariant)</c>, so 'daily' — the tool's own advertised example —
/// threw <see cref="FormatException"/>, which the SDK masks. Decision: no daily/onlogon (that is a
/// feature); the refusal becomes an <see cref="ArgumentException"/> naming <c>trigger</c> and the
/// accepted form, thrown before anything is registered, and the description stops advertising
/// what the code does not accept.
/// </summary>
[Trait("Category", "Unit")]
public class TaskSchedulerTriggerTests
{
    [Theory]
    [InlineData("daily")]          // the description's own example
    [InlineData("onlogon")]        // ditto
    [InlineData("tomorrow")]
    [InlineData("2026-13-45")]     // the right shape, not a date
    [InlineData("")]
    [InlineData("   ")]
    public void A_trigger_that_is_not_a_date_and_time_is_an_ArgumentException_naming_trigger_and_the_accepted_form(string trigger)
    {
        var act = () => TaskSchedulerService.ParseTrigger(trigger);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be("trigger");
        ToolErrors.IsCallerFacing(ex).Should().BeTrue("a FormatException reached the caller as a crash");
        ex.Message.Should().Contain("trigger");
        ex.Message.Should().Contain($"'{trigger}'", "the caller sees what it sent");
        ex.Message.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}", "the accepted form, shown by example");
    }

    [Fact]
    public void An_ISO_8601_date_and_time_parses()
        => TaskSchedulerService.ParseTrigger("2026-05-06T09:00:00").Should().Be(new DateTime(2026, 5, 6, 9, 0, 0));

    [Fact]
    public void Parsing_stays_invariant_whatever_the_thread_culture()
    {
        // The reason the service parsed with InvariantCulture: "05/06/2026" is 6 May under the
        // invariant culture and 5 June under en-GB. The refactor must not lose it.
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-GB");
            TaskSchedulerService.ParseTrigger("05/06/2026 09:00").Should().Be(new DateTime(2026, 5, 6, 9, 0, 0));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void The_trigger_parameter_description_advertises_only_what_the_code_accepts()
    {
        var trigger = typeof(ProcessTools).GetMethod(nameof(ProcessTools.ScheduledTask))!
            .GetParameters().Single(p => p.Name == "trigger")
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        trigger.Should().NotContainEquivalentOf("daily", "'daily' is refused");
        trigger.Should().NotContainEquivalentOf("onlogon", "'onlogon' is refused");
        trigger.Should().ContainEquivalentOf("date", "the one accepted form is a date and time");
    }
}

/// <summary>
/// D-10 R13 through the real Task Scheduler: the refusal comes BEFORE anything is registered.
/// Self-cleaning — the task name is a fresh GUID, and Dispose deletes it if a regression ever did
/// register it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TaskSchedulerCreateRefusalIntegrationTests : IDisposable
{
    private readonly string _name = "wmcp-test-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try
        {
            using var ts = new TaskService();
            if (ts.GetTask(_name) is not null) ts.RootFolder.DeleteTask(_name, exceptionOnNotExists: false);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public async System.Threading.Tasks.Task Create_with_trigger_daily_is_refused_naming_trigger_and_registers_nothing()
    {
        var svc = new TaskSchedulerService();

        Func<System.Threading.Tasks.Task> act = () => svc.CreateAsync(_name, "cmd.exe /c exit 0", "daily");

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("trigger");
        using var ts = new TaskService();
        ts.GetTask(_name).Should().BeNull("a refused create must not leave a task behind");
    }

    /// <summary>
    /// GREEN round 2: the accepted form still registers, and the trigger is the time ParseTrigger
    /// read — not "now", not a culture-dependent reading. Far in the future and disabled-by-nothing
    /// is fine: Dispose deletes the task long before 2099.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task Create_with_a_date_and_time_registers_one_time_trigger_at_that_time()
    {
        var svc = new TaskSchedulerService();

        await svc.CreateAsync(_name, "cmd.exe", "2099-05-06T09:00:00");

        using var ts = new TaskService();
        var task = ts.GetTask(_name);
        task.Should().NotBeNull("an accepted trigger registers the task");
        var trigger = task!.Definition.Triggers.Should().ContainSingle().Which.Should().BeOfType<TimeTrigger>().Which;
        trigger.StartBoundary.Should().Be(new DateTime(2099, 5, 6, 9, 0, 0));
        task.Definition.Actions.OfType<ExecAction>().Should().ContainSingle().Which.Path.Should().Be("cmd.exe");
    }
}
