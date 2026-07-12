using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ProcessTools
{
    private readonly IProcessService _process;
    private readonly IServiceControlService _service;
    private readonly ITaskSchedulerService _scheduler;
    private readonly IEventLogService _eventLog;

    public ProcessTools(
        IProcessService process,
        IServiceControlService service,
        ITaskSchedulerService scheduler,
        IEventLogService eventLog)
    {
        _process = process;
        _service = service;
        _scheduler = scheduler;
        _eventLog = eventLog;
    }

    [McpServerTool, Description(
        "List/inspect/kill processes. actions: list|orphans|kill. " +
        "list: plain (Pid,Name,Path,MemoryMb); with includeLineage:true adds parentPid, parentName, " +
        "commandLine, startTime, ageMinutes, orphaned, runtimeKind, isSystemAdjacent, rootPid, " +
        "memoryMb; with groupByRoot:true returns processes collapsed under their nearest-live root " +
        "ancestor. orphans: lineage rows where the parent is gone (recycle-aware: parent absent, or " +
        "a live same-PID process started AFTER the child). NOTE orphaned is COMMON and by-design on " +
        "Windows (explorer.exe and apps from a closed shell are orphaned) — it is NOT a leak signal; " +
        "use the signals to rank, the tool does not judge. name filters list/orphans by substring, " +
        "case-insensitive: on name OR command line for orphans/includeLineage/groupByRoot, on name " +
        "only for the plain list (a plain row carries no command line). With groupByRoot it returns " +
        "the whole trees that contain a match (full membership, true DescendantCount) — it does not " +
        "trim the tree. kill: by pid or name (EXACT name match, kills all matching), confirm:true " +
        "required. " +
        "tree:true and startTime apply to pid-based kills ONLY (an error is raised if given with " +
        "name and no pid). tree:true kills the pid AND its descendants (leaves-first, each " +
        "re-validated against the snapshot before killing). startTime (ISO-8601) guards against PID " +
        "reuse — the kill aborts unless the live process's start time matches.")]
    public async Task<string> Process(
        [Description("Action: list, orphans, or kill")] string action,
        [Description("Process name; kill target, or substring filter for list/orphans")] string? name = null,
        [Description("Process ID (kill target)")] int? pid = null,
        [Description("Must be true to confirm a kill")] bool confirm = false,
        [Description("list: include parent lineage + signals")] bool includeLineage = false,
        [Description("list: group processes under their root ancestor")] bool groupByRoot = false,
        [Description("kill: also kill the target's descendants")] bool tree = false,
        [Description("kill: ISO-8601 start time guard against PID reuse")] string? startTime = null,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                if (groupByRoot)
                    return JsonSerializer.Serialize(await _process.GroupByRootAsync(name, ct));
                if (includeLineage)
                    return JsonSerializer.Serialize(await _process.ListLineageAsync(false, name, ct));
                return JsonSerializer.Serialize(await _process.ListAsync(name, ct));

            case "orphans":
                return JsonSerializer.Serialize(await _process.ListLineageAsync(true, name, ct));

            case "kill":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for kill");
                DateTime? start = null;
                if (!string.IsNullOrWhiteSpace(startTime))
                {
                    if (!DateTime.TryParse(startTime, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        throw new ArgumentException($"'startTime' must be ISO-8601, got: '{startTime}'");
                    start = parsed.ToUniversalTime();
                }
                if (pid.HasValue)
                {
                    if (tree)
                    {
                        int n = await _process.KillTreeAsync(pid.Value, start, ct);
                        return $"killed {n} process(es) in tree of pid {pid.Value}";
                    }
                    if (start is DateTime s)
                    {
                        await _process.KillGuardedAsync(pid.Value, s, ct);
                        return $"killed pid {pid.Value} (start-time verified)";
                    }
                    await _process.KillAsync(pid.Value, ct);
                    return $"killed pid {pid.Value}";
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    // tree/startTime are pid-only — refuse rather than silently ignore them on a
                    // name-based kill (a caller expecting tree semantics must not get a plain kill).
                    if (tree || start is not null)
                        throw new ArgumentException("'tree' and 'startTime' require 'pid' (they do not apply to name-based kills)");
                    // Deliberately unfiltered: a kill matches the name EXACTLY. Reusing the list's
                    // substring filter here would make `kill --name node` also kill `node-inspector`.
                    var all = await _process.ListAsync(null, ct);
                    var targets = all.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    foreach (var t in targets)
                        await _process.KillAsync(t.Pid, ct);
                    return $"killed {targets.Length} process(es) named '{name}'";
                }
                throw new ArgumentException("'kill' requires either name or pid");

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|orphans|kill");
        }
    }

    [McpServerTool, Description("Deep-inspect a process by PID: parent PID, command line, start time, and the loaded-module (DLL) inventory. Use to spot injected/sideloaded DLLs or trace a process's lineage. The module list may be unavailable (ModulesError set) for protected or higher-integrity processes.")]
    public async Task<string> ProcessInspect(
        [Description("Process ID to inspect")] int pid,
        CancellationToken ct = default)
    {
        var detail = await _process.InspectAsync(pid, ct);
        return JsonSerializer.Serialize(detail);
    }

    [McpServerTool, Description("Start a process detached from the MCP server. Returns the PID.")]
    public async Task<string> StartProcess(
        [Description("Command line to execute (exe + args)")] string command,
        CancellationToken ct = default)
    {
        int pid = await _process.StartDetachedAsync(command, ct);
        return $"started (pid={pid})";
    }

    [McpServerTool, Description("Manage Windows services. action: list|status|start|stop|restart. stop and restart require confirm:true.")]
    public async Task<string> Service(
        [Description("Action: list, status, start, stop, restart")] string action,
        [Description("Service name (required for status/start/stop/restart)")] string? name = null,
        [Description("Must be true to confirm stop or restart")] bool confirm = false,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                var services = await _service.ListAsync(ct);
                return JsonSerializer.Serialize(services);

            case "status":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'status' requires name");
                var status = await _service.GetStatusAsync(name, ct);
                return JsonSerializer.Serialize(status);

            case "start":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'start' requires name");
                await _service.StartAsync(name, ct);
                return $"started '{name}'";

            case "stop":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for stop/restart actions");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'stop' requires name");
                await _service.StopAsync(name, ct);
                return $"stopped '{name}'";

            case "restart":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for stop/restart actions");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'restart' requires name");
                await _service.RestartAsync(name, ct);
                return $"restarted '{name}'";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|status|start|stop|restart");
        }
    }

    [McpServerTool, Description("Manage Windows scheduled tasks. action: list|get|run|create|delete. delete requires confirm:true.")]
    public async Task<string> ScheduledTask(
        [Description("Action: list, get, run, create, delete")] string action,
        [Description("Task name (required for get/run/create/delete)")] string? name = null,
        [Description("Command for create action")] string? command = null,
        [Description("Trigger for create action (e.g. 'daily', 'onlogon')")] string? trigger = null,
        [Description("Must be true to confirm destructive delete action")] bool confirm = false,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                var tasks = await _scheduler.ListAsync(ct);
                return JsonSerializer.Serialize(tasks);

            case "get":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'get' requires name");
                var task = await _scheduler.GetAsync(name, ct);
                return JsonSerializer.Serialize(task);

            case "run":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'run' requires name");
                await _scheduler.RunAsync(name, ct);
                return $"ran task '{name}'";

            case "create":
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(trigger))
                    throw new ArgumentException("'create' requires name, command, and trigger");
                await _scheduler.CreateAsync(name, command, trigger, ct);
                return $"created task '{name}'";

            case "delete":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for delete");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'delete' requires name");
                await _scheduler.DeleteAsync(name, ct);
                return $"deleted task '{name}'";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|get|run|create|delete");
        }
    }

    [McpServerTool, Description("Query Windows Event Log. log: Application|System|Security. level: error|warning|information. since: ISO 8601 datetime.")]
    public async Task<string> EventLog(
        [Description("Event log name: Application, System, Security, etc.")] string log,
        [Description("Filter by level: error, warning, information")] string? level = null,
        [Description("Filter by source name")] string? source = null,
        [Description("Filter events since this datetime (ISO 8601 format)")] string? since = null,
        [Description("Maximum number of entries to return")] int max = 100,
        CancellationToken ct = default)
    {
        DateTime? sinceDate = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                throw new ArgumentException($"'since' must be a valid ISO 8601 datetime, got: '{since}'");
            sinceDate = parsed;
        }

        var entries = await _eventLog.QueryAsync(log, level, source, sinceDate, max, ct);
        return JsonSerializer.Serialize(entries);
    }
}
