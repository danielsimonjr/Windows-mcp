using WinTask = Microsoft.Win32.TaskScheduler.Task;
using Microsoft.Win32.TaskScheduler;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

// Suppress ambiguous-reference: TaskScheduler library exports 'Task' which clashes with
// System.Threading.Tasks.Task. We alias the library type above and use System.Threading.Tasks
// explicitly via 'return System.Threading.Tasks.Task.FromResult(...)' patterns.
using SystemTask = System.Threading.Tasks.Task;

namespace WindowsMcp.Services;

public sealed class TaskSchedulerService : ITaskSchedulerService
{
    public System.Threading.Tasks.Task<ScheduledTaskDto[]> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var tasks = ts.RootFolder.AllTasks
            .Select(t => new ScheduledTaskDto(t.Name, t.Path, t.State.ToString(), t.LastRunTime, t.NextRunTime))
            .ToArray();
        return System.Threading.Tasks.Task.FromResult(tasks);
    }

    public System.Threading.Tasks.Task<ScheduledTaskDetailDto[]> ListDetailedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var list = new List<ScheduledTaskDetailDto>();
        foreach (var t in ts.AllTasks)
        {
            ct.ThrowIfCancellationRequested();
            string? actionPath = null;
            string? actionArgs = null;
            string[] triggers = Array.Empty<string>();
            try
            {
                var def = t.Definition;
                var exec = def.Actions.OfType<ExecAction>().FirstOrDefault();
                if (exec is not null)
                {
                    actionPath = exec.Path;
                    actionArgs = string.IsNullOrEmpty(exec.Arguments) ? null : exec.Arguments;
                }
                triggers = def.Triggers.Select(tr => tr.TriggerType.ToString()).Distinct().ToArray();
            }
            catch
            {
                // Protected/corrupt task definition: emit name/path/state only.
            }
            list.Add(new ScheduledTaskDetailDto(t.Name, t.Path, t.State.ToString(), actionPath, actionArgs, triggers));
        }
        return System.Threading.Tasks.Task.FromResult(list.ToArray());
    }

    public System.Threading.Tasks.Task<ScheduledTaskDto> GetAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var t = ts.GetTask(name)
            ?? throw new KeyNotFoundException($"Scheduled task '{name}' not found");
        return System.Threading.Tasks.Task.FromResult(
            new ScheduledTaskDto(t.Name, t.Path, t.State.ToString(), t.LastRunTime, t.NextRunTime));
    }

    public SystemTask RunAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var t = ts.GetTask(name) ?? throw new KeyNotFoundException(name);
        t.Run();
        return SystemTask.CompletedTask;
    }

    /// <summary>
    /// D-10 R13: the one trigger form <c>create</c> accepts — a date and time, parsed with the
    /// invariant culture — or an <see cref="ArgumentException"/> naming <c>trigger</c> and the
    /// accepted form, thrown before anything is registered.
    /// </summary>
    internal static DateTime ParseTrigger(string trigger)
    {
        // InvariantCulture: MCP tool args arrive as locale-neutral JSON strings;
        // thread culture could otherwise parse "05/06/2026" differently on en-US vs en-GB.
        if (!string.IsNullOrWhiteSpace(trigger)
            && DateTime.TryParse(trigger, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var when))
            return when;
        throw new ArgumentException(
            $"trigger '{trigger}' is not a date and time. The only trigger create accepts is a one-time start, " +
            "e.g. 2026-05-06T09:00:00; recurring triggers (daily, at logon) are not supported.", nameof(trigger));
    }

    public SystemTask CreateAsync(string name, string command, string trigger, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var start = ParseTrigger(trigger);   // D-10 R13: refused before the scheduler is touched
        using var ts = new TaskService();
        var td = ts.NewTask();
        td.Actions.Add(new ExecAction(command));
        td.Triggers.Add(new TimeTrigger(start));
        ts.RootFolder.RegisterTaskDefinition(name, td);
        return SystemTask.CompletedTask;
    }

    public SystemTask DeleteAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        ts.RootFolder.DeleteTask(name);
        return SystemTask.CompletedTask;
    }
}
