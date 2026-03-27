using Xunit;
using Mesen.Annotation;

namespace Mesen.Tests.Annotation;

/// <summary>
/// Pinned-value tests ensure our InOutPoint flags are byte-for-byte compatible
/// with DiztinGUIsh's InOutPoint enum.
/// </summary>
public class InOutPointTests
{
    // ── Pinned values (must match DiztinGUIsh InOutPoint exactly) ─────────────

    [Theory]
    [InlineData(InOutPoint.None,      0x00)]
    [InlineData(InOutPoint.InPoint,   0x01)]
    [InlineData(InOutPoint.OutPoint,  0x02)]
    [InlineData(InOutPoint.EndPoint,  0x04)]
    [InlineData(InOutPoint.ReadPoint, 0x08)]
    public void InOutPoint_HasExactDizValue(InOutPoint flag, byte expected)
    {
        Assert.Equal(expected, (byte)flag);
    }

    // ── Flag combinations ─────────────────────────────────────────────────────

    [Fact]
    public void InOutPoint_InAndOut_Combines()
    {
        var combined = InOutPoint.InPoint | InOutPoint.OutPoint;
        Assert.Equal((byte)0x03, (byte)combined);
    }

    [Fact]
    public void InOutPoint_InAndEnd_Combines()
    {
        var combined = InOutPoint.InPoint | InOutPoint.EndPoint;
        Assert.Equal((byte)0x05, (byte)combined);
    }

    [Fact]
    public void InOutPoint_AllFlags_Combines()
    {
        var all = InOutPoint.InPoint | InOutPoint.OutPoint | InOutPoint.EndPoint | InOutPoint.ReadPoint;
        Assert.Equal((byte)0x0F, (byte)all);
    }

    // ── HasFlag isolation ─────────────────────────────────────────────────────

    [Fact]
    public void InOutPoint_HasFlag_TrueWhenSet()
    {
        var flags = InOutPoint.InPoint | InOutPoint.ReadPoint;
        Assert.True(flags.HasFlag(InOutPoint.InPoint));
        Assert.True(flags.HasFlag(InOutPoint.ReadPoint));
    }

    [Fact]
    public void InOutPoint_HasFlag_FalseWhenNotSet()
    {
        var flags = InOutPoint.InPoint | InOutPoint.ReadPoint;
        Assert.False(flags.HasFlag(InOutPoint.OutPoint));
        Assert.False(flags.HasFlag(InOutPoint.EndPoint));
    }

    // ── Default ───────────────────────────────────────────────────────────────

    [Fact]
    public void InOutPoint_Default_IsNone()
    {
        Assert.Equal(InOutPoint.None, default(InOutPoint));
    }
}
