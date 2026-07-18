using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// Reads the NTFS USN change journal via DeviceIoControl. Uses raw byte buffers + BitConverter
/// rather than struct marshalling — the struct layouts are stable and byte parsing avoids the
/// silent-stack-corruption class of interop bugs. The buffer parser (<see cref="ParseReadBuffer"/>)
/// and reason formatter are pure statics, unit-tested against crafted buffers; the native path is
/// exercised live (it needs an elevated volume handle a CI runner won't have).
/// </summary>
public sealed class UsnService : IUsnService
{
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_RW = 0x00000003;
    private const uint OPEN_EXISTING = 3;
    private const int ReadBufferSize = 64 * 1024;

    public Task<UsnStatus> StatusAsync(string volume, CancellationToken ct = default) => Task.Run(() => Status(volume), ct);

    public Task<UsnReadResult> ReadAsync(string volume, long startUsn, int max, CancellationToken ct = default)
        => Task.Run(() => Read(volume, startUsn, max <= 0 ? 200 : max, ct), ct);

    private static UsnStatus Status(string volume)
    {
        using var h = OpenVolume(volume);
        var jd = QueryJournal(h, volume);
        return new UsnStatus(NormalizeVolume(volume), jd.JournalId, jd.FirstUsn, jd.NextUsn, jd.LowestValidUsn);
    }

    private static UsnReadResult Read(string volume, long startUsn, int max, CancellationToken ct)
    {
        using var h = OpenVolume(volume);
        var jd = QueryJournal(h, volume);
        long cursor = startUsn <= 0 ? jd.FirstUsn : startUsn;
        long nextUsn = cursor;

        var changes = new List<UsnChange>();
        var outBuf = new byte[ReadBufferSize];
        while (changes.Count < max)
        {
            ct.ThrowIfCancellationRequested();
            var inBuf = BuildReadInput(cursor, jd.JournalId);
            if (!DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, out uint returned, IntPtr.Zero))
                throw new InvalidOperationException($"READ_USN_JOURNAL failed on {volume} (error {Marshal.GetLastWin32Error()}).");
            if (returned <= 8) break; // header only, no records
            var (batchNext, batch) = ParseReadBuffer(outBuf, (int)returned, max - changes.Count);
            changes.AddRange(batch);
            nextUsn = batchNext;
            if (batchNext <= cursor || batch.Count == 0) break; // no forward progress
            cursor = batchNext;
            if (cursor >= jd.NextUsn) break; // reached the end that existed at query time
        }
        return new UsnReadResult(NormalizeVolume(volume), nextUsn, changes.Count, changes.ToArray());
    }

    /// <summary>Parse a READ_USN_JOURNAL output buffer: 8-byte NextUsn header then packed USN_RECORD_V2 entries.</summary>
    public static (long NextUsn, List<UsnChange> Changes) ParseReadBuffer(byte[] buf, int length, int max)
    {
        var changes = new List<UsnChange>();
        if (length < 8) return (0, changes);
        long nextUsn = BitConverter.ToInt64(buf, 0);
        int off = 8;
        while (off + 60 <= length && changes.Count < max)
        {
            int recLen = BitConverter.ToInt32(buf, off);
            if (recLen < 60 || off + recLen > length) break;
            long usn = BitConverter.ToInt64(buf, off + 24);
            long ts = BitConverter.ToInt64(buf, off + 32);
            uint reason = BitConverter.ToUInt32(buf, off + 40);
            ushort nameLen = BitConverter.ToUInt16(buf, off + 56);
            ushort nameOff = BitConverter.ToUInt16(buf, off + 58);
            string name = (nameLen > 0 && off + nameOff + nameLen <= off + recLen)
                ? Encoding.Unicode.GetString(buf, off + nameOff, nameLen)
                : string.Empty;
            DateTime when;
            try { when = DateTime.FromFileTimeUtc(ts); } catch { when = DateTime.MinValue; }
            changes.Add(new UsnChange(name, FormatReason(reason), usn, when));
            off += recLen;
        }
        return (nextUsn, changes);
    }

    /// <summary>Map a USN reason bitmask to a pipe-joined set of human labels.</summary>
    public static string FormatReason(uint reason)
    {
        var parts = new List<string>();
        void Add(uint flag, string name) { if ((reason & flag) != 0) parts.Add(name); }
        Add(0x00000001, "data-overwrite"); Add(0x00000002, "data-extend"); Add(0x00000004, "data-truncation");
        Add(0x00000100, "file-create"); Add(0x00000200, "file-delete");
        Add(0x00000800, "security-change"); Add(0x00001000, "rename-old-name"); Add(0x00002000, "rename-new-name");
        Add(0x00008000, "basic-info-change"); Add(0x00200000, "stream-change"); Add(0x80000000, "close");
        return parts.Count == 0 ? $"0x{reason:x8}" : string.Join("|", parts);
    }

    private readonly record struct JournalData(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn);

    private static JournalData QueryJournal(SafeFileHandle h, string volume)
    {
        var outBuf = new byte[80];
        if (!DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, null, 0, outBuf, (uint)outBuf.Length, out _, IntPtr.Zero))
            throw new InvalidOperationException(
                $"QUERY_USN_JOURNAL failed on {volume} (error {Marshal.GetLastWin32Error()}); the volume may have no active USN journal.");
        return new JournalData(
            BitConverter.ToUInt64(outBuf, 0),
            BitConverter.ToInt64(outBuf, 8),
            BitConverter.ToInt64(outBuf, 16),
            BitConverter.ToInt64(outBuf, 24));
    }

    private static byte[] BuildReadInput(long startUsn, ulong journalId)
    {
        var b = new byte[40];
        BitConverter.GetBytes(startUsn).CopyTo(b, 0);      // StartUsn
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(b, 8);   // ReasonMask (all)
        BitConverter.GetBytes(0u).CopyTo(b, 12);           // ReturnOnlyOnClose
        BitConverter.GetBytes(0UL).CopyTo(b, 16);          // Timeout
        BitConverter.GetBytes(0UL).CopyTo(b, 24);          // BytesToWaitFor
        BitConverter.GetBytes(journalId).CopyTo(b, 32);    // UsnJournalID
        return b;
    }

    private static SafeFileHandle OpenVolume(string volume)
    {
        string dev = @"\\.\" + NormalizeVolume(volume);
        var h = CreateFileW(dev, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
            throw new InvalidOperationException($"Cannot open volume {dev} (error {Marshal.GetLastWin32Error()}); USN journal access requires elevation.");
        return h;
    }

    private static string NormalizeVolume(string volume)
    {
        var v = (volume ?? "C").Trim().TrimEnd('\\', '/').TrimEnd(':');
        if (v.Length == 0) v = "C";
        return string.Concat(v.AsSpan(0, 1).ToString().ToUpperInvariant(), ":");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
