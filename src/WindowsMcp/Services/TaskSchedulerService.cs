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
                else
                {
                    var com = def.Actions.OfType<ComHandlerAction>().FirstOrDefault();
                    if (com is not null)
                    {
                        actionPath = ComHandlerResolver.Resolve(com.ClassId) ?? $"CLSID:{com.ClassId:B}";
                        actionArgs = string.IsNullOrEmpty(com.Data) ? null : com.Data;
                    }
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

    public SystemTask CreateAsync(string name, string command, string trigger, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var td = ts.NewTask();
        td.Actions.Add(new ExecAction(command));
        td.Triggers.Add(ParseTrigger(trigger));
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

    internal static Trigger ParseTrigger(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
            throw new ArgumentException("Trigger cannot be empty");

        return trigger.Trim().ToLowerInvariant() switch
        {
            "daily" => new DailyTrigger(),
            "onlogon" or "logon" => new LogonTrigger(),
            "onboot" or "boot" => new BootTrigger(),
            "onidle" or "idle" => new IdleTrigger(),
            _ when DateTime.TryParse(trigger, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                => new TimeTrigger(when),
            _ => throw new ArgumentException(
                $"Unknown trigger '{trigger}'; expected daily|onlogon|onboot|onidle or an ISO-8601 datetime")
        };
    }
}
