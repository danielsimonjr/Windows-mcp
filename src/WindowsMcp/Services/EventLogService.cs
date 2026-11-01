using System.Diagnostics;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class EventLogService : IEventLogService
{
    public Task<EventLogEntryDto[]> QueryAsync(string log, string? level, string? source, DateTime? since, int max, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        max = Math.Clamp(max, 1, 1000);
        using var el = new EventLog(log);
        var entries = el.Entries.Cast<EventLogEntry>()
            .Where(e => since == null || e.TimeGenerated >= since.Value)
            .Where(e => source == null || e.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
            .Where(e => level == null || e.EntryType.ToString().Equals(level, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.TimeGenerated)
            .Take(max)
            .Select(e => new EventLogEntryDto(
                Id:      (int)e.InstanceId,
                Source:  e.Source,
                Message: e.Message,
                Level:   e.EntryType.ToString(),
                Time:    e.TimeGenerated))
            .ToArray();
        return Task.FromResult(entries);
    }
}
