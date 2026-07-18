using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class EventRingBufferTests
{
    private static WatchEvent Ev(string p) => new("changed", p, DateTime.UtcNow);

    [Fact]
    public void Add_within_capacity_keeps_all_and_drops_none()
    {
        var b = new EventRingBuffer(5);
        b.Add(Ev("a")); b.Add(Ev("b"));
        b.Count.Should().Be(2);
        b.Dropped.Should().Be(0);
    }

    [Fact]
    public void Add_beyond_capacity_drops_oldest_and_counts_it()
    {
        var b = new EventRingBuffer(2);
        b.Add(Ev("a")); b.Add(Ev("b")); b.Add(Ev("c")); // "a" dropped

        b.Count.Should().Be(2);
        b.Dropped.Should().Be(1);
        b.Drain(10).Select(e => e.Path).Should().Equal("b", "c");
    }

    [Fact]
    public void Drain_returns_fifo_and_removes()
    {
        var b = new EventRingBuffer(10);
        b.Add(Ev("a")); b.Add(Ev("b")); b.Add(Ev("c"));

        b.Drain(2).Select(e => e.Path).Should().Equal("a", "b");
        b.Count.Should().Be(1);
        b.Drain(0).Select(e => e.Path).Should().Equal("c"); // max<=0 drains the rest
        b.Count.Should().Be(0);
    }
}
