using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;

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

    [McpServerTool(Title = "Processes", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "List/inspect/kill processes. actions: list|orphans|kill. " +
        "list: plain (Pid,Name,Path,MemoryMb,CpuPercent — CPU is measured over a 250 ms window, " +
        "normalised across all cores; sort_by memory (default)|cpu|name|pid and limit (0 = all; pass " +
        "20 for a short answer) apply to the plain list only); with includeLineage:true adds parentPid, parentName, " +
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
        "reuse — the kill aborts unless the live process's start time matches. graceful:true asks the " +
        "process to close (WM_CLOSE to its visible windows, so an editor can show its save prompt), " +
        "waits grace_ms (default 3000, at most 60000) and only then forces it; a process with no window " +
        "is forced at once and the result says so; grace_ms applies per process on a name kill, and " +
        "cancelling the request during the wait leaves the process asked to close but not killed. pid " +
        "and name kills return {killed:[{pid, name, graceful, exitedGracefully, forced, waitedMs}]}; " +
        "graceful cannot be combined with tree, and includeLineage cannot be combined with groupByRoot.")]
    public async Task<string> Process(
        [Description("Action: list, orphans, or kill")] string action,
        [Description("Process name; kill target, or substring filter for list/orphans")] string? name = null,
        [Description("Process ID (kill target)")] int? pid = null,
        [Description("Must be true to confirm a kill")] bool confirm = false,
        [Description("list: include parent lineage + signals")] bool includeLineage = false,
        [Description("list: group processes under their root ancestor")] bool groupByRoot = false,
        [Description("kill: also kill the target's descendants")] bool tree = false,
        [Description("kill: ISO-8601 start time guard against PID reuse")] string? startTime = null,
        [Description("list (plain only): memory (default), cpu, name, or pid; blank is the same as omitted")] string? sort_by = null,
        [Description("list (plain only): at most this many rows after the sort; 0 = all")] int limit = 0,
        [Description("kill: ask the process to close before forcing it (not with tree)")] bool graceful = false,
        [Description("kill: how long a graceful close may take before the process is forced, 0-60000 ms")] int grace_ms = 3000,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                if (groupByRoot && includeLineage)
                    throw new ArgumentException(
                        "'includeLineage' and 'groupByRoot' are two different shapes; pass one of them, not both");
                if (groupByRoot || includeLineage)
                {
                    RefusePlainListOptions(sort_by, limit, groupByRoot ? "groupByRoot" : "includeLineage");
                    if (groupByRoot)
                        return JsonSerializer.Serialize(await _process.GroupByRootAsync(name, ct));
                    return JsonSerializer.Serialize(await _process.ListLineageAsync(false, name, ct));
                }
                var options = new ProcessListOptions(name, ParseSort(sort_by), ParseLimit(limit));
                return JsonSerializer.Serialize(await _process.ListAsync(options, ct));

            case "orphans":
                RefusePlainListOptions(sort_by, limit, "orphans");
                if (includeLineage)
                    throw new ArgumentException("'includeLineage' applies to list only; orphans already carries the lineage columns", nameof(includeLineage));
                if (groupByRoot)
                    throw new ArgumentException("'groupByRoot' applies to list only, not to orphans", nameof(groupByRoot));
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
                if (grace_ms is < 0 or > 60_000)
                    throw new ArgumentException($"'grace_ms' must be between 0 and 60000 ms, got {grace_ms}", nameof(grace_ms));
                if (graceful && tree)
                    throw new ArgumentException("'graceful' cannot be combined with 'tree': descendants are killed leaves-first and forcibly");
                var killOptions = new KillOptions(graceful, grace_ms, start);
                if (pid.HasValue)
                {
                    if (tree)
                    {
                        int n = await _process.KillTreeAsync(pid.Value, start, ct);
                        return $"killed {n} process(es) in tree of pid {pid.Value}";
                    }
                    var one = await _process.KillAsync(pid.Value, killOptions, ct);
                    return JsonSerializer.Serialize(new { killed = new[] { KillRow(one) } });
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    // tree/startTime are pid-only — refuse rather than silently ignore them on a
                    // name-based kill (a caller expecting tree semantics must not get a plain kill).
                    if (tree || start is not null)
                        throw new ArgumentException("'tree' and 'startTime' require 'pid' (they do not apply to name-based kills)");
                    // Deliberately unfiltered: a kill matches the name EXACTLY. Reusing the list's
                    // substring filter here would make `kill --name node` also kill `node-inspector`.
                    // The old overload: a name kill must not pay the CPU sample window.
                    var all = await _process.ListAsync((string?)null, ct);
                    var targets = all.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var killed = new List<object>(targets.Length);
                    foreach (var t in targets)
                        killed.Add(KillRow(await _process.KillAsync(t.Pid, killOptions, ct)));
                    return JsonSerializer.Serialize(new { killed });
                }
                throw new ArgumentException("'kill' requires either name or pid");

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|orphans|kill");
        }
    }

    // ---- C-3 helpers -----------------------------------------------------------------------

    private static ProcessSort ParseSort(string? sortBy) => sortBy?.Trim().ToLowerInvariant() switch
    {
        null or "" or "memory" => ProcessSort.Memory,
        "cpu" => ProcessSort.Cpu,
        "name" => ProcessSort.Name,
        "pid" => ProcessSort.Pid,
        _ => throw new ArgumentException($"'sort_by' must be one of memory|cpu|name|pid, got '{sortBy}'", nameof(sortBy)),
    };

    private static int ParseLimit(int limit) => limit >= 0
        ? limit
        : throw new ArgumentException($"'limit' must be 0 (all rows) or more, got {limit}", nameof(limit));

    /// <summary>The lineage, group and orphan shapes have no CPU column and their own order: refuse rather than ignore.</summary>
    private static void RefusePlainListOptions(string? sortBy, int limit, string shape)
    {
        // Blank means "not given", exactly as ParseSort reads it for the plain list.
        if (!string.IsNullOrWhiteSpace(sortBy))
            throw new ArgumentException($"'sort_by' applies to the plain list only, not with {shape}", nameof(sortBy));
        if (limit != 0)
            throw new ArgumentException($"'limit' applies to the plain list only, not with {shape}", nameof(limit));
    }

    private static object KillRow(KillResult r) => new
    {
        pid = r.Pid,
        name = r.Name,
        graceful = r.Graceful,
        exitedGracefully = r.ExitedGracefully,
        forced = r.Forced,
        waitedMs = r.WaitedMs,
    };

    [McpServerTool(Title = "Inspect process", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Deep-inspect a process by PID: parent PID, command line, start time, and the loaded-module (DLL) inventory. Use to spot injected/sideloaded DLLs or trace a process's lineage. The module list may be unavailable (ModulesError set) for protected or higher-integrity processes.")]
    public async Task<string> ProcessInspect(
        [Description("Process ID to inspect")] int pid,
        CancellationToken ct = default)
    {
        var detail = await _process.InspectAsync(pid, ct);
        return JsonSerializer.Serialize(detail);
    }

    [McpServerTool(Title = "Start process", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true), Description("Start a process detached from the MCP server. Either one command line (exe + args, split at the first space or after a quoted exe) or, with args_json, the executable plus a JSON array of arguments passed verbatim with no quoting; cwd sets the working directory and must exist; use_shell_execute launches through the shell. Returns {pid, executable, args, cwd}.")]
    public async Task<string> StartProcess(
        [Description("Command line to execute (exe + args), or the executable alone when args_json is given")] string command,
        [Description("Arguments as a JSON array of strings, e.g. [\"/c\",\"echo hi\"]; each item is passed verbatim, no quoting needed")] string? args_json = null,
        [Description("Working directory for the new process; must exist")] string? cwd = null,
        [Description("Launch through the shell (ShellExecute) instead of directly")] bool use_shell_execute = false,
        CancellationToken ct = default)
    {
        var args = ArgvJson.Parse(args_json);   // refused by name before anything is spawned
        var spec = new ProcessStart(command, args, string.IsNullOrWhiteSpace(cwd) ? null : cwd, use_shell_execute);
        int pid = await _process.StartDetachedAsync(spec, ct);
        return JsonSerializer.Serialize(new { pid, executable = command, args = args ?? [], cwd = spec.Cwd });
    }

    [McpServerTool(Title = "Windows service", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Manage Windows services. action: list|status|start|stop|restart. stop and restart require confirm:true.")]
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

    [McpServerTool(Title = "Scheduled task", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Manage Windows scheduled tasks. action: list|get|run|create|delete. delete requires confirm:true.")]
    public async Task<string> ScheduledTask(
        [Description("Action: list, get, run, create, delete")] string action,
        [Description("Task name (required for get/run/create/delete)")] string? name = null,
        [Description("Command for create action")] string? command = null,
        [Description("For create: the one-time start as a date and time, e.g. '2026-05-06T09:00:00'. Recurring triggers are not supported")] string? trigger = null,
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

    [McpServerTool(Title = "Event log", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Query Windows Event Log. log: Application|System|Security. level: error|warning|information. since: ISO 8601 datetime.")]
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
