using System.Management;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>Typed projection of a Win32_Process row (parse boundary handles CIM_DATETIME).</summary>
public readonly record struct Win32ProcRow(
    int Pid, int ParentPid, string Name, DateTime? CreationUtc, string? CommandLine, long MemoryMb);

/// <summary>Pure process-lineage logic: parse, orphan classification, root grouping, signals.</summary>
public static class ProcessLineage
{
    /// <summary>Project one raw WMI dictionary row into a typed row; null if it has no ProcessId.</summary>
    public static Win32ProcRow? From(IDictionary<string, object> row)
    {
        if (!row.TryGetValue("ProcessId", out var pidObj) || pidObj is null) return null;
        int pid = Convert.ToInt32(pidObj);
        int ppid = row.TryGetValue("ParentProcessId", out var pp) && pp is not null ? Convert.ToInt32(pp) : 0;
        string name = row.TryGetValue("Name", out var nm) && nm is not null ? nm.ToString()! : "";
        string? cmd = row.TryGetValue("CommandLine", out var cl) ? cl?.ToString() : null;

        DateTime? created = null;
        if (row.TryGetValue("CreationDate", out var cd) && cd is string s && s.Length > 0)
        {
            try { created = ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime(); }
            catch { /* unparseable CIM_DATETIME -> null */ }
        }

        long memMb = 0;
        if (row.TryGetValue("WorkingSetSize", out var ws) && ws is not null)
        {
            try { memMb = Convert.ToInt64(ws) / 1024 / 1024; } catch { /* leave 0 */ }
        }
        return new Win32ProcRow(pid, ppid, name, created, cmd, memMb);
    }

    public static ProcessLineageDto[] Classify(IReadOnlyList<Win32ProcRow> rows, DateTime nowUtc)
    {
        var byId = new Dictionary<int, Win32ProcRow>();
        foreach (var r in rows) byId[r.Pid] = r;

        bool ParentAlive(Win32ProcRow p)
            => byId.TryGetValue(p.ParentPid, out var par) && !IsRecycledParent(p, par);

        int RootOf(Win32ProcRow p)
        {
            var seen = new HashSet<int>();
            var cur = p;
            int hops = 0;
            while (ParentAlive(cur) && hops++ < 64 && seen.Add(cur.Pid))
                cur = byId[cur.ParentPid];
            return cur.Pid;
        }

        var result = new List<ProcessLineageDto>(rows.Count);
        foreach (var p in rows)
        {
            bool alive = ParentAlive(p);
            string? parentName = alive && byId.TryGetValue(p.ParentPid, out var par) ? par.Name : null;
            int? age = p.CreationUtc is DateTime c
                ? (int)Math.Max(0, (nowUtc - c).TotalMinutes) : null;
            result.Add(new ProcessLineageDto(
                p.Pid, p.Name, p.ParentPid, parentName, p.CommandLine, p.CreationUtc, age,
                !alive, RuntimeKind(p.Name), IsSystemAdjacent(p), RootOf(p), p.MemoryMb));
        }
        return result.ToArray();
    }

    /// <summary>Substring match on process name OR command line, case-insensitive.</summary>
    public static bool Matches(ProcessLineageDto p, string filter) =>
        p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (p.CommandLine?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Collapse processes under their nearest-live root ancestor. When <paramref name="nameFilter"/>
    /// is set, only groups containing at least one matching process are returned — each still with
    /// its FULL membership and true DescendantCount. The filter selects which trees to show; it
    /// never trims a tree, because a trimmed count still reads as "descendants" and would mislead.
    /// </summary>
    public static ProcessGroupDto[] GroupByRoot(ProcessLineageDto[] procs, string? nameFilter = null)
    {
        var byId = procs.ToDictionary(p => p.Pid);
        IEnumerable<IGrouping<int, ProcessLineageDto>> groups = procs.GroupBy(p => p.RootPid);

        if (!string.IsNullOrWhiteSpace(nameFilter))
            groups = groups.Where(g => g.Any(p => Matches(p, nameFilter)));

        return groups
            .Select(g =>
            {
                byId.TryGetValue(g.Key, out var root);
                return new ProcessGroupDto(g.Key, root?.Name ?? "", root?.StartTimeUtc,
                    g.Count(), g.Select(x => x.Pid).OrderBy(x => x).ToArray());
            })
            .OrderByDescending(x => x.DescendantCount).ToArray();
    }

    static readonly Dictionary<string, string> KindMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node.exe"] = "node",
        ["python.exe"] = "python", ["python3.exe"] = "python", ["pythonw.exe"] = "python",
        ["dotnet.exe"] = "dotnet",
        ["pwsh.exe"] = "shell", ["powershell.exe"] = "shell", ["cmd.exe"] = "shell",
        ["bash.exe"] = "shell", ["wsl.exe"] = "shell",
        ["chrome.exe"] = "browser", ["msedge.exe"] = "browser", ["firefox.exe"] = "browser",
    };

    static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "svchost.exe", "fontdrvhost.exe", "dwm.exe",
        "userinit.exe", "explorer.exe",
    };

    public static string RuntimeKind(string name)
    {
        if (KindMap.TryGetValue(name, out var k)) return k;
        if (name.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return "python";
        return SystemNames.Contains(name) ? "native" : "other";
    }

    public static bool IsSystemAdjacent(Win32ProcRow p)
        => SystemNames.Contains(p.Name) || p.ParentPid is 0 or 4;

    /// <summary>
    /// True when <paramref name="parent"/> is provably a recycled PID rather than the real parent
    /// of <paramref name="child"/> — i.e. both creation times are known and the "parent" started
    /// AFTER the child. A null date on either side cannot prove recycling, so returns false.
    /// Single source of truth for the recycle rule (used by both lineage classification and the
    /// kill-tree descendant walk).
    /// </summary>
    public static bool IsRecycledParent(Win32ProcRow child, Win32ProcRow parent)
        => child.CreationUtc is DateTime c && parent.CreationUtc is DateTime pc && pc > c;
}
