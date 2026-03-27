using Xunit;
using Mesen.Annotation;

namespace Mesen.Tests.Annotation;

/// <summary>
/// Pinned-value tests ensure our ByteType enum is byte-for-byte compatible with
/// DiztinGUIsh's FlagType. If these break, import/export will silently corrupt
/// project files.
/// </summary>
public class ByteTypeTests
{
    // ── Pinned values (must match DiztinGUIsh FlagType exactly) ───────────────

    [Theory]
    [InlineData(ByteType.Unreached, 0x00)]
    [InlineData(ByteType.Opcode,    0x10)]
    [InlineData(ByteType.Operand,   0x11)]
    [InlineData(ByteType.Data8,     0x20)]
    [InlineData(ByteType.Graphics,  0x21)]
    [InlineData(ByteType.Music,     0x22)]
    [InlineData(ByteType.Empty,     0x23)]
    [InlineData(ByteType.Data16,    0x30)]
    [InlineData(ByteType.Pointer16, 0x31)]
    [InlineData(ByteType.Data24,    0x40)]
    [InlineData(ByteType.Pointer24, 0x41)]
    [InlineData(ByteType.Data32,    0x50)]
    [InlineData(ByteType.Pointer32, 0x51)]
    [InlineData(ByteType.Text,      0x60)]
    public void ByteType_HasExactDizValue(ByteType type, byte expected)
    {
        Assert.Equal(expected, (byte)type);
    }

    // ── No duplicate underlying values ────────────────────────────────────────

    [Fact]
    public void ByteType_AllValuesAreDistinct()
    {
        var values = Enum.GetValues<ByteType>().Select(v => (byte)v).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    // ── IsCode ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ByteType.Opcode,  true)]
    [InlineData(ByteType.Operand, true)]
    public void IsCode_TrueForCodeTypes(ByteType type, bool expected)
    {
        Assert.Equal(expected, type.IsCode());
    }

    [Theory]
    [InlineData(ByteType.Unreached)]
    [InlineData(ByteType.Data8)]
    [InlineData(ByteType.Graphics)]
    [InlineData(ByteType.Music)]
    [InlineData(ByteType.Empty)]
    [InlineData(ByteType.Data16)]
    [InlineData(ByteType.Pointer16)]
    [InlineData(ByteType.Data24)]
    [InlineData(ByteType.Pointer24)]
    [InlineData(ByteType.Data32)]
    [InlineData(ByteType.Pointer32)]
    [InlineData(ByteType.Text)]
    public void IsCode_FalseForNonCodeTypes(ByteType type)
    {
        Assert.False(type.IsCode());
    }

    // ── IsData ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ByteType.Data8)]
    [InlineData(ByteType.Graphics)]
    [InlineData(ByteType.Music)]
    [InlineData(ByteType.Empty)]
    [InlineData(ByteType.Data16)]
    [InlineData(ByteType.Pointer16)]
    [InlineData(ByteType.Data24)]
    [InlineData(ByteType.Pointer24)]
    [InlineData(ByteType.Data32)]
    [InlineData(ByteType.Pointer32)]
    [InlineData(ByteType.Text)]
    public void IsData_TrueForDataTypes(ByteType type)
    {
        Assert.True(type.IsData());
    }

    [Theory]
    [InlineData(ByteType.Unreached)]
    [InlineData(ByteType.Opcode)]
    [InlineData(ByteType.Operand)]
    public void IsData_FalseForUnreachedAndCode(ByteType type)
    {
        Assert.False(type.IsData());
    }

    // ── Exhaustive partition: every value is exactly one of {Unreached, Code, Data} ──

    [Fact]
    public void ByteType_EveryValueBelongsToExactlyOneCategory()
    {
        foreach (var type in Enum.GetValues<ByteType>())
        {
            var categories = new[]
            {
                type == ByteType.Unreached,
                type.IsCode(),
                type.IsData(),
            };
            Assert.Equal(1, categories.Count(c => c));
        }
    }
}
