using System.Text;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class UsnServiceTests
{
    [Theory]
    [InlineData(0x00000100u, "file-create")]
    [InlineData(0x00000200u, "file-delete")]
    [InlineData(0x00000001u, "data-overwrite")]
    [InlineData(0u, "0x00000000")]
    public void FormatReason_maps_single_flags(uint reason, string expected)
        => UsnService.FormatReason(reason).Should().Be(expected);

    [Fact]
    public void FormatReason_joins_multiple_flags()
        => UsnService.FormatReason(0x00000100u | 0x80000000u)
            .Should().Be("file-create|close");

    [Fact]
    public void ParseReadBuffer_reads_a_synthetic_record()
    {
        var buf = BuildBuffer(nextUsn: 99, name: "hi.txt", usn: 42, reason: 0x00000100u);

        var (next, changes) = UsnService.ParseReadBuffer(buf, buf.Length, 10);

        next.Should().Be(99);
        changes.Should().ContainSingle();
        changes[0].FileName.Should().Be("hi.txt");
        changes[0].Usn.Should().Be(42);
        changes[0].Reasons.Should().Be("file-create");
    }

    [Fact]
    public void ParseReadBuffer_respects_max()
    {
        // two records back-to-back
        var r1 = BuildRecord("a.txt", 1, 0x100u);
        var r2 = BuildRecord("b.txt", 2, 0x200u);
        var buf = new byte[8 + r1.Length + r2.Length];
        BitConverter.GetBytes(7L).CopyTo(buf, 0);
        r1.CopyTo(buf, 8);
        r2.CopyTo(buf, 8 + r1.Length);

        var (_, changes) = UsnService.ParseReadBuffer(buf, buf.Length, 1);
        changes.Should().ContainSingle();
        changes[0].FileName.Should().Be("a.txt");
    }

    [Fact]
    public void ParseReadBuffer_short_buffer_yields_nothing()
    {
        var (next, changes) = UsnService.ParseReadBuffer(new byte[4], 4, 10);
        next.Should().Be(0);
        changes.Should().BeEmpty();
    }

    private static byte[] BuildBuffer(long nextUsn, string name, long usn, uint reason)
    {
        var rec = BuildRecord(name, usn, reason);
        var buf = new byte[8 + rec.Length];
        BitConverter.GetBytes(nextUsn).CopyTo(buf, 0);
        rec.CopyTo(buf, 8);
        return buf;
    }

    // A minimal USN_RECORD_V2: name at offset 60, 8-byte-aligned RecordLength.
    private static byte[] BuildRecord(string name, long usn, uint reason)
    {
        const ushort nameOff = 60;
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int recLen = (nameOff + nameBytes.Length + 7) & ~7;
        var rec = new byte[recLen];
        BitConverter.GetBytes(recLen).CopyTo(rec, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(rec, 4);                       // MajorVersion
        BitConverter.GetBytes(usn).CopyTo(rec, 24);                           // Usn
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(rec, 32); // TimeStamp
        BitConverter.GetBytes(reason).CopyTo(rec, 40);                        // Reason
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(rec, 56);     // FileNameLength
        BitConverter.GetBytes(nameOff).CopyTo(rec, 58);                      // FileNameOffset
        nameBytes.CopyTo(rec, nameOff);
        return rec;
    }
}
