using System.Security.Cryptography;
using System.Text.Json;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class IntegrityService : IIntegrityService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _storeDir;
    private readonly Func<IEnumerable<string>> _defaultPaths;

    public IntegrityService() : this(null, null) { }

    // Test seam: override the store directory and the default watch-list.
    public IntegrityService(string? storeDir, Func<IEnumerable<string>>? defaultPaths)
    {
        _storeDir = storeDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "windows-mcp", "integrity");
        _defaultPaths = defaultPaths ?? BuildDefaultWatchList;
    }

    private string BaselinePath => Path.Combine(_storeDir, "baseline.json");

    public string[] DefaultWatchList() =>
        _defaultPaths().Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<IntegrityBaseline> BaselineAsync(IEnumerable<string>? extraPaths = null, CancellationToken ct = default)
    {
        var roots = _defaultPaths().Concat(extraPaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var items = new List<IntegrityItem>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in ExpandToFiles(root))
                items.Add(await HashItemAsync(file, ct));
        }

        var baseline = new IntegrityBaseline(
            DateTime.UtcNow, roots,
            items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToArray());

        Directory.CreateDirectory(_storeDir);
        await File.WriteAllTextAsync(BaselinePath, JsonSerializer.Serialize(baseline, JsonOpts), ct);
        return baseline;
    }

    public IntegrityBaseline? GetBaseline()
    {
        if (!File.Exists(BaselinePath)) return null;
        try { return JsonSerializer.Deserialize<IntegrityBaseline>(File.ReadAllText(BaselinePath), JsonOpts); }
        catch { return null; }
    }

    public async Task<IntegrityCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var baseline = GetBaseline();
        var now = DateTime.UtcNow;
        if (baseline is null)
            return new IntegrityCheckResult(null, now, false, 0, Array.Empty<IntegrityChange>());

        var changes = new List<IntegrityChange>();
        var known = new HashSet<string>(baseline.Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var unchanged = 0;

        // 1) Each recorded item, re-checked by path.
        foreach (var old in baseline.Items)
        {
            ct.ThrowIfCancellationRequested();
            var cur = await HashItemAsync(old.Path, ct);
            if (old.Exists && !cur.Exists)
                changes.Add(new IntegrityChange(old.Path, "removed", old.Sha256, null));
            else if (old.Exists && cur.Exists && !HashEquals(old.Sha256, cur.Sha256))
                changes.Add(new IntegrityChange(old.Path, "modified", old.Sha256, cur.Sha256));
            else if (!old.Exists && cur.Exists)
                changes.Add(new IntegrityChange(old.Path, "added", null, cur.Sha256));
            else
                unchanged++;
        }

        // 2) New files that appeared under any watched directory root.
        foreach (var root in baseline.Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in SafeEnumerateFiles(root))
            {
                if (known.Contains(file)) continue;
                ct.ThrowIfCancellationRequested();
                var cur = await HashItemAsync(file, ct);
                changes.Add(new IntegrityChange(file, "added", null, cur.Sha256));
            }
        }

        return new IntegrityCheckResult(
            baseline.CreatedUtc, now, true, unchanged,
            changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool HashEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // At baseline time: a directory expands to the files directly in it; a file (or an absent
    // path we still want to watch) is recorded as-is so its later change/removal/appearance shows.
    private static IEnumerable<string> ExpandToFiles(string root)
    {
        if (Directory.Exists(root)) return SafeEnumerateFiles(root);
        return new[] { root };
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    private static async Task<IntegrityItem> HashItemAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return new IntegrityItem(path, false, null, 0, null);
            var fi = new FileInfo(path);
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return new IntegrityItem(path, true, Convert.ToHexString(hash), fi.Length, fi.LastWriteTimeUtc);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new IntegrityItem(path, false, null, 0, null); }
    }

    // Curated persistence/config spots a Tech-Guru tripwire guards out of the box.
    private static IEnumerable<string> BuildDefaultWatchList()
    {
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new[]
        {
            Path.Combine(winDir, @"System32\drivers\etc\hosts"),
            Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\Startup"),
            Path.Combine(programData, @"Microsoft\Windows\Start Menu\Programs\Startup"),
            Path.Combine(up, @".claude\settings.json"),
            Path.Combine(up, ".gitconfig"),
            @"C:\AGENTS.md", @"C:\CLAUDE.md", @"C:\MEMORY.md", @"C:\TODO.md", @"C:\CHANGELOG.md",
        };
    }
}
