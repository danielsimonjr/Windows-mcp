using System.Collections.Generic;
using System.Diagnostics;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class ProcessService : IProcessService
{
    private readonly IWmiService _wmi;
    private readonly IProcessWindowNative _windows;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>C-3 (roadmap R4): the window between the two CPU-time readings of a plain list.</summary>
    internal static readonly TimeSpan CpuSampleWindow = TimeSpan.FromMilliseconds(250);

    public ProcessService(IWmiService wmi) : this(wmi, Win32ProcessWindowNative.Instance) { }

    /// <summary>
    /// C-3: the testable constructor — the window seam the graceful kill posts through and the
    /// delay the CPU sample uses, so neither needs a desktop or a real 250 ms.
    /// </summary>
    internal ProcessService(IWmiService wmi, IProcessWindowNative windows, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _wmi = wmi;
        _windows = windows;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// C-3: the plain list with a CPU column — two <c>TotalProcessorTime</c> readings across one
    /// <see cref="CpuSampleWindow"/>, normalised by <see cref="CpuSample.Percent"/> — sorted and
    /// capped by the options. A process that exits between the readings reads 0. The old
    /// <c>ListAsync(nameFilter)</c> overload does not sample (a name kill enumerates through it
    /// and must not pay the window).
    /// </summary>
    public async Task<ProcessDto[]> ListAsync(ProcessListOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var processes = Process.GetProcesses();
        try
        {
            IEnumerable<Process> q = processes;
            if (!string.IsNullOrWhiteSpace(options.NameFilter))
                q = q.Where(p => p.ProcessName.Contains(options.NameFilter, StringComparison.OrdinalIgnoreCase));
            var candidates = q.ToArray();

            // Each reading is timestamped per process: walking a few hundred processes takes
            // tens of milliseconds at either end, so one shared elapsed time would credit the
            // processes read first with CPU time from a longer window than it divides by.
            var before = new Dictionary<int, (TimeSpan Cpu, long At)>(candidates.Length);
            foreach (var p in candidates)
            {
                try { before[p.Id] = (p.TotalProcessorTime, Stopwatch.GetTimestamp()); }
                catch { /* exited, or a protected process: no CPU column for it */ }
            }

            await _delay(CpuSampleWindow, ct);

            var percent = new Dictionary<int, double>(candidates.Length);
            int cores = Environment.ProcessorCount;
            foreach (var p in candidates)
            {
                if (!before.TryGetValue(p.Id, out var first)) continue;
                try
                {
                    p.Refresh();
                    var after = p.TotalProcessorTime;
                    var elapsed = Stopwatch.GetElapsedTime(first.At);
                    percent[p.Id] = CpuSample.Percent(first.Cpu, after, elapsed, cores);
                }
                catch { /* exited between the readings */ }
            }

            var rows = new List<ProcessDto>(candidates.Length);
            foreach (var p in candidates)
            {
                try
                {
                    double cpu = percent.GetValueOrDefault(p.Id);
                    string? path = null;
                    try { path = p.MainModule?.FileName; }
                    catch { /* system/protected processes throw on MainModule access */ }
                    rows.Add(new ProcessDto(p.Id, p.ProcessName, path, p.WorkingSet64 / 1024 / 1024, cpu));
                }
                catch (InvalidOperationException)
                {
                    // Gone since the snapshot; a row for a process that no longer exists helps no one.
                }
            }
            return CpuSample.SortAndLimit(rows, options.SortBy, options.Limit);
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    /// <summary>
    /// C-3 (roadmap R5): the start-time guard first when given (a mismatch kills nothing); then,
    /// when graceful, <c>WM_CLOSE</c> to every visible top-level window of the pid through the
    /// seam, a bounded wait for the exit, and <c>Kill()</c> only if it is still there. A process
    /// with nothing to close is killed at once and the result says so.
    /// </summary>
    public async Task<KillResult> KillAsync(int pid, KillOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var proc = Process.GetProcessById(pid);   // ArgumentException if gone
        string? name = null;
        try { name = proc.ProcessName; } catch { /* exited */ }

        if (options.ExpectedStartUtc is DateTime expected)
        {
            DateTime actual;
            try { actual = proc.StartTime.ToUniversalTime(); }
            catch { throw new InvalidOperationException($"cannot read start time of pid {pid} to verify"); }
            if (Math.Abs((actual - expected).TotalSeconds) > 1.5)
                throw new InvalidOperationException(
                    $"pid {pid} start time {actual:o} != expected {expected:o}; aborting (possible PID reuse)");
        }

        if (!options.Graceful)
        {
            proc.Kill();
            return new KillResult(pid, name, false, false, true, 0);
        }

        int posted = 0;
        foreach (var hwnd in _windows.TopLevelWindowsOf(pid))
        {
            if (_windows.PostClose(hwnd)) posted++;
        }
        if (posted == 0)
        {
            proc.Kill();
            return new KillResult(pid, name, true, false, true, 0);
        }

        var clock = Stopwatch.StartNew();
        bool exited;
        using (var grace = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            grace.CancelAfter(Math.Max(options.GraceMs, 0));
            try
            {
                await proc.WaitForExitAsync(grace.Token);
                exited = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                exited = false;
            }
        }
        int waited = (int)clock.ElapsedMilliseconds;
        if (!exited)
        {
            // Round 4: the wait timed out, but the process may have answered the close in the
            // meantime. A process that is gone now closed late; it was not forced.
            proc.Refresh();
            exited = proc.HasExited;
        }
        if (exited)
            return new KillResult(pid, name, true, true, false, waited);

        proc.Kill();   // does not throw for a process that exited meanwhile (verified on .NET 10)
        return new KillResult(pid, name, true, false, true, waited);
    }

    public Task<ProcessDto[]> ListAsync(string? nameFilter = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Process wrappers hold native handles (opened on WorkingSet64/MainModule access);
        // dispose every one after projecting to DTOs, or handles leak per call.
        var processes = Process.GetProcesses();
        try
        {
            IEnumerable<Process> q = processes;

            // Filter on the name BEFORE projecting: MainModule access opens a native handle and
            // throws on protected processes, so skipping non-matches is both cheaper and quieter.
            if (!string.IsNullOrWhiteSpace(nameFilter))
                q = q.Where(p => p.ProcessName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

            var dtos = q
                .Select(p =>
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; }
                    catch { /* system/protected processes throw on MainModule access */ }
                    return new ProcessDto(p.Id, p.ProcessName, path, p.WorkingSet64 / 1024 / 1024);
                })
                .ToArray();

            return Task.FromResult(dtos);
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    public Task KillAsync(int pid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.GetProcessById(pid);
        process.Kill();
        return Task.CompletedTask;
    }

    public Task<int> StartDetachedAsync(string command, CancellationToken ct = default)
        => StartDetachedAsync(new ProcessStart(command, null, null, false), ct);

    /// <summary>
    /// The pre-B-11 split of a whole command line: a quoted executable then the rest, or the
    /// first space. Unchanged, so every caller of the command-only form gets what it always got.
    /// </summary>
    private static (string Exe, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
                throw new ArgumentException("Unmatched opening quote in command");
            return (command[1..closingQuote], command[(closingQuote + 1)..].TrimStart());
        }
        int spaceIdx = command.IndexOf(' ');
        return spaceIdx < 0
            ? (command, string.Empty)
            : (command[..spaceIdx], command[(spaceIdx + 1)..].TrimStart());
    }

    /// <summary>
    /// B-11: the <see cref="ProcessStartInfo"/> a <see cref="ProcessStart"/> describes — pure, so
    /// the argv/cwd/shell-execute decisions are testable without spawning anything. Also the one
    /// place a missing working directory is refused, which is what makes "rejected before any
    /// spawn" a fact rather than a hope.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(ProcessStart spec)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = spec.UseShellExecute,
            RedirectStandardOutput = false,
            CreateNoWindow = false,
        };

        if (spec.Args is null)
        {
            // Command-only: the whole line, split as it always was.
            var (exe, args) = SplitCommand(spec.Command);
            psi.FileName = exe;
            psi.Arguments = args;
        }
        else
        {
            // Argv mode: the command is the executable and every item is one argument, verbatim.
            psi.FileName = spec.Command;
            foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(spec.Cwd))
        {
            if (!Directory.Exists(spec.Cwd))
                throw new DirectoryNotFoundException($"cwd '{spec.Cwd}' is not an existing directory.");
            psi.WorkingDirectory = spec.Cwd;
        }

        return psi;
    }

    public Task<int> StartDetachedAsync(ProcessStart spec, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var psi = BuildStartInfo(spec);   // refuses a bad cwd before anything is spawned

        // Detached child keeps running after we dispose the wrapper; dispose only releases our handle.
        // D-10 R12/R26: a command that does not exist is a FileNotFoundException naming the file
        // that was actually tried — for an unquoted command line that is the text before the first
        // space, so say how to pass a path that contains one.
        bool splitAtSpace = spec.Args is null && !spec.Command.StartsWith('"') && spec.Command.Contains(' ');
        using var process = ProcessStartErrors.Start(psi, psi.FileName, splitAtSpace
            ? "The command line was split at its first space: if the path contains spaces, quote it, or pass the executable as command and its arguments in args_json."
            : null);

        return Task.FromResult(process.Id);
    }

    public async Task<ProcessDetailDto> InspectAsync(int pid, CancellationToken ct = default)
    {
        // Parent PID + command line come from WMI (System.Diagnostics exposes neither).
        int? parentPid = null;
        string? commandLine = null;
        string? name = null;

        var rows = await _wmi.QueryAsync("Win32_Process", null, $"ProcessId={pid}", ct);
        if (rows.Length > 0 && rows[0] is IDictionary<string, object> d)
        {
            if (d.TryGetValue("ParentProcessId", out var pp) && pp is not null)
                parentPid = Convert.ToInt32(pp);
            if (d.TryGetValue("CommandLine", out var cl))
                commandLine = cl?.ToString();
            if (d.TryGetValue("Name", out var nm))
                name = nm?.ToString();
        }

        // Start time + loaded modules come from the live Process. Module enumeration throws
        // Access-Denied on protected/higher-integrity processes — capture that as a note rather
        // than failing the whole inspection.
        DateTime? startUtc = null;
        var modules = new List<ModuleInfo>();
        string? modulesError = null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            name ??= proc.ProcessName;
            try { startUtc = proc.StartTime.ToUniversalTime(); } catch { /* may be denied */ }
            try
            {
                foreach (ProcessModule m in proc.Modules)
                    modules.Add(new ModuleInfo(m.ModuleName, m.FileName));
            }
            catch (Exception ex)
            {
                modulesError = ex.Message;
            }
        }
        catch (ArgumentException)
        {
            // Process exited between the WMI query and here; return what WMI gave us.
        }

        return new ProcessDetailDto(pid, name, parentPid, commandLine, startUtc, modulesError, modules.ToArray());
    }

    private async Task<List<Win32ProcRow>> SnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // entry check for every lineage/group/kill-tree caller
        var raw = await _wmi.QueryAsync("Win32_Process", null, null, ct);
        return raw.OfType<IDictionary<string, object>>()
            .Select(ProcessLineage.From)
            .Where(r => r.HasValue).Select(r => r!.Value).ToList();
    }

    public async Task<ProcessLineageDto[]> ListLineageAsync(bool orphansOnly, string? nameFilter, CancellationToken ct = default)
    {
        var all = ProcessLineage.Classify(await SnapshotAsync(ct), DateTime.UtcNow);
        IEnumerable<ProcessLineageDto> q = all;
        if (orphansOnly) q = q.Where(p => p.Orphaned);
        if (!string.IsNullOrWhiteSpace(nameFilter))
            q = q.Where(p => ProcessLineage.Matches(p, nameFilter));
        return q.ToArray();
    }

    public async Task<ProcessGroupDto[]> GroupByRootAsync(string? nameFilter = null, CancellationToken ct = default)
        => ProcessLineage.GroupByRoot(
            ProcessLineage.Classify(await SnapshotAsync(ct), DateTime.UtcNow), nameFilter);

    public Task KillGuardedAsync(int pid, DateTime expectedStartUtc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var proc = Process.GetProcessById(pid); // ArgumentException if gone
        DateTime actual;
        try { actual = proc.StartTime.ToUniversalTime(); }
        catch { throw new InvalidOperationException($"cannot read start time of pid {pid} to verify"); }
        if (Math.Abs((actual - expectedStartUtc).TotalSeconds) > 1.5)
            throw new InvalidOperationException(
                $"pid {pid} start time {actual:o} != expected {expectedStartUtc:o}; aborting (possible PID reuse)");
        proc.Kill();
        return Task.CompletedTask;
    }

    public async Task<int> KillTreeAsync(int pid, DateTime? expectedStartUtc, CancellationToken ct = default)
    {
        var rows = await SnapshotAsync(ct);
        var byId = rows.ToDictionary(r => r.Pid);
        if (!byId.TryGetValue(pid, out var root))
            throw new ArgumentException($"pid {pid} not found");
        if (expectedStartUtc is DateTime exp)
        {
            // Caller asked for a start-time guard — honor it or refuse; never silently proceed.
            if (root.CreationUtc is not DateTime rc)
                throw new InvalidOperationException(
                    $"pid {pid} start time unavailable; cannot honor the requested startTime guard, aborting");
            if (Math.Abs((rc - exp).TotalSeconds) > 1.5)
                throw new InvalidOperationException($"pid {pid} start time mismatch; aborting");
        }

        var childrenOf = new Dictionary<int, List<int>>();
        foreach (var r in rows)
            if (byId.TryGetValue(r.ParentPid, out var par) && !ProcessLineage.IsRecycledParent(r, par))
            {
                if (!childrenOf.TryGetValue(r.ParentPid, out var list))
                    childrenOf[r.ParentPid] = list = new List<int>();
                list.Add(r.Pid);
            }

        var order = new List<int>();
        var seen = new HashSet<int>();
        void Visit(int id)
        {
            if (!seen.Add(id)) return;
            if (childrenOf.TryGetValue(id, out var kids))
                foreach (var k in kids) Visit(k);
            order.Add(id); // post-order => leaves first
        }
        Visit(pid);

        int killed = 0;
        foreach (var id in order)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                // Re-validate before killing: the live process must still be the same one we
                // snapshotted. A PID reused between the snapshot and here would otherwise be an
                // innocent bystander. Skip (do not kill) on a start-time mismatch. When the
                // snapshot had no CIM date (boot/protected proc), we cannot prove reuse, so proceed.
                if (byId.TryGetValue(id, out var snap) && snap.CreationUtc is DateTime sc)
                {
                    DateTime live;
                    try { live = p.StartTime.ToUniversalTime(); }
                    catch { continue; } // cannot verify identity -> do not kill
                    if (Math.Abs((live - sc).TotalSeconds) > 1.5) continue; // PID reused -> skip
                }
                p.Kill();
                killed++;
            }
            catch { /* already exited */ }
        }
        return killed;
    }
}
