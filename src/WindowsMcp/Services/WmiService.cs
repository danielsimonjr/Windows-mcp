using System.Management;
using System.Text.RegularExpressions;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class WmiService : IWmiService
{
    private static readonly Regex ClassNamePattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NamespacePattern =
        new(@"^root(\\[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WherePattern =
        new(@"^[A-Za-z0-9_.()\s='"":<>!&|%-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<object[]> QueryAsync(string className, string? @namespace = null, string? where = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateQuery(className, @namespace, where);

        var ns = @namespace ?? "root\\cimv2";
        var wql = string.IsNullOrWhiteSpace(where)
            ? $"SELECT * FROM {className}"
            : $"SELECT * FROM {className} WHERE {where}";

        var scope = new ManagementScope(ns);
        var query = new ObjectQuery(wql);

        using var searcher = new ManagementObjectSearcher(scope, query);

        // ManagementObjectCollection and each ManagementObject are COM-backed and disposable;
        // project to plain dictionaries, then dispose every row + the collection.
        using var collection = searcher.Get();
        var rows = new List<object>();
        foreach (ManagementObject mo in collection)
        {
            using (mo)
            {
                rows.Add(mo.Properties
                    .Cast<PropertyData>()
                    .ToDictionary(p => p.Name, p => p.Value));
            }
        }

        return Task.FromResult(rows.ToArray());
    }

    internal static void ValidateQuery(string className, string? @namespace, string? where)
    {
        if (string.IsNullOrWhiteSpace(className) || !ClassNamePattern.IsMatch(className))
            throw new ArgumentException(
                $"Invalid WMI class name '{className}'; expected alphanumeric identifier like Win32_Process");

        var ns = @namespace ?? "root\\cimv2";
        if (!NamespacePattern.IsMatch(ns))
            throw new ArgumentException(
                $"Invalid WMI namespace '{ns}'; expected form like root\\cimv2");

        if (!string.IsNullOrWhiteSpace(where))
        {
            if (where.Contains(';', StringComparison.Ordinal)
                || where.Contains("--", StringComparison.Ordinal)
                || where.Contains("/*", StringComparison.Ordinal)
                || where.Contains("*/", StringComparison.Ordinal))
                throw new ArgumentException("WMI WHERE clause contains disallowed characters");

            if (!WherePattern.IsMatch(where))
                throw new ArgumentException("WMI WHERE clause contains unsupported syntax");
        }
    }
}
