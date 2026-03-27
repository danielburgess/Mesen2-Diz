using Xunit;
using Mesen.Annotation;

namespace Mesen.Tests.Annotation;

public class ByteAnnotationTests
{
    // ── Default construction ──────────────────────────────────────────────────

    [Fact]
    public void Default_HasUnreachedType()
    {
        Assert.Equal(ByteType.Unreached, default(ByteAnnotation).Type);
    }

    [Fact]
    public void Default_AllFlagsAreFalseOrZero()
    {
        var a = default(ByteAnnotation);
        Assert.False(a.MFlag);
        Assert.False(a.XFlag);
        Assert.Equal(0x00, a.DataBank);
        Assert.Equal((ushort)0x0000, a.DirectPage);
        Assert.Equal(InOutPoint.None, a.Flow);
        Assert.Equal(CpuArch.Cpu65C816, a.Arch);
    }

    [Fact]
    public void Default_StaticProperty_EqualsDefaultKeyword()
    {
        Assert.Equal(default(ByteAnnotation), ByteAnnotation.Default);
    }

    // ── Field round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void FieldRoundTrip_AllNonDefaultValues()
    {
        var a = new ByteAnnotation
        {
            Type       = ByteType.Opcode,
            MFlag      = true,
            XFlag      = true,
            DataBank   = 0x7E,
            DirectPage = 0x0100,
            Flow       = InOutPoint.InPoint | InOutPoint.OutPoint,
            Arch       = CpuArch.Spc700,
        };

        Assert.Equal(ByteType.Opcode,                          a.Type);
        Assert.True(a.MFlag);
        Assert.True(a.XFlag);
        Assert.Equal(0x7E,                                     a.DataBank);
        Assert.Equal((ushort)0x0100,                           a.DirectPage);
        Assert.Equal(InOutPoint.InPoint | InOutPoint.OutPoint, a.Flow);
        Assert.Equal(CpuArch.Spc700,                           a.Arch);
    }

    // ── With* mutators ────────────────────────────────────────────────────────

    [Fact]
    public void WithType_ChangesTypeOnly()
    {
        var original = ByteAnnotation.Default;
        var modified = original.WithType(ByteType.Data16);

        Assert.Equal(ByteType.Data16, modified.Type);
        Assert.Equal(original.MFlag,      modified.MFlag);
        Assert.Equal(original.XFlag,      modified.XFlag);
        Assert.Equal(original.DataBank,   modified.DataBank);
        Assert.Equal(original.DirectPage, modified.DirectPage);
        Assert.Equal(original.Flow,       modified.Flow);
        Assert.Equal(original.Arch,       modified.Arch);
    }

    [Fact]
    public void WithMFlag_ChangesMFlagOnly()
    {
        var modified = ByteAnnotation.Default.WithMFlag(true);
        Assert.True(modified.MFlag);
        Assert.Equal(ByteAnnotation.Default.Type,       modified.Type);
        Assert.Equal(ByteAnnotation.Default.XFlag,      modified.XFlag);
        Assert.Equal(ByteAnnotation.Default.DataBank,   modified.DataBank);
        Assert.Equal(ByteAnnotation.Default.DirectPage, modified.DirectPage);
        Assert.Equal(ByteAnnotation.Default.Flow,       modified.Flow);
        Assert.Equal(ByteAnnotation.Default.Arch,       modified.Arch);
    }

    [Fact]
    public void WithXFlag_ChangesXFlagOnly()
    {
        var modified = ByteAnnotation.Default.WithXFlag(true);
        Assert.True(modified.XFlag);
        Assert.Equal(ByteAnnotation.Default.MFlag, modified.MFlag);
    }

    [Fact]
    public void WithDataBank_ChangesDataBankOnly()
    {
        var modified = ByteAnnotation.Default.WithDataBank(0x3F);
        Assert.Equal(0x3F, modified.DataBank);
        Assert.Equal(ByteAnnotation.Default.Type, modified.Type);
    }

    [Fact]
    public void WithDirectPage_ChangesDirectPageOnly()
    {
        var modified = ByteAnnotation.Default.WithDirectPage(0xFFFF);
        Assert.Equal((ushort)0xFFFF, modified.DirectPage);
        Assert.Equal(ByteAnnotation.Default.Type, modified.Type);
    }

    [Fact]
    public void WithFlow_ChangesFlowOnly()
    {
        var modified = ByteAnnotation.Default.WithFlow(InOutPoint.EndPoint);
        Assert.Equal(InOutPoint.EndPoint, modified.Flow);
        Assert.Equal(ByteAnnotation.Default.Type, modified.Type);
    }

    [Fact]
    public void WithArch_ChangesArchOnly()
    {
        var modified = ByteAnnotation.Default.WithArch(CpuArch.SuperFx);
        Assert.Equal(CpuArch.SuperFx, modified.Arch);
        Assert.Equal(ByteAnnotation.Default.Type, modified.Type);
    }

    // ── Value type semantics ──────────────────────────────────────────────────

    [Fact]
    public void Copy_IsIndependentOfOriginal()
    {
        var original = ByteAnnotation.Default;
        var copy = original;
        var modified = copy.WithType(ByteType.Graphics);

        // original must be unchanged
        Assert.Equal(ByteType.Unreached, original.Type);
        Assert.Equal(ByteType.Graphics,  modified.Type);
    }

    // ── Equality ──────────────────────────────────────────────────────────────

    [Fact]
    public void Equality_SameFields_AreEqual()
    {
        var a = new ByteAnnotation { Type = ByteType.Opcode, MFlag = true, DataBank = 0x01 };
        var b = new ByteAnnotation { Type = ByteType.Opcode, MFlag = true, DataBank = 0x01 };
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_DifferentType_NotEqual()
    {
        var a = ByteAnnotation.Default.WithType(ByteType.Opcode);
        var b = ByteAnnotation.Default.WithType(ByteType.Data8);
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Theory]
    [InlineData(true,  false)]  // MFlag differs
    [InlineData(false, true)]   // XFlag differs
    public void Equality_DifferentCpuFlags_NotEqual(bool mFlag, bool xFlag)
    {
        var a = ByteAnnotation.Default.WithMFlag(mFlag).WithXFlag(xFlag);
        var b = ByteAnnotation.Default;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_DifferentDataBank_NotEqual()
    {
        Assert.NotEqual(
            ByteAnnotation.Default.WithDataBank(0x00),
            ByteAnnotation.Default.WithDataBank(0x7E));
    }

    [Fact]
    public void Equality_DifferentDirectPage_NotEqual()
    {
        Assert.NotEqual(
            ByteAnnotation.Default.WithDirectPage(0x0000),
            ByteAnnotation.Default.WithDirectPage(0x0100));
    }

    [Fact]
    public void Equality_DifferentFlow_NotEqual()
    {
        Assert.NotEqual(
            ByteAnnotation.Default.WithFlow(InOutPoint.None),
            ByteAnnotation.Default.WithFlow(InOutPoint.InPoint));
    }

    [Fact]
    public void Equality_DifferentArch_NotEqual()
    {
        Assert.NotEqual(
            ByteAnnotation.Default.WithArch(CpuArch.Cpu65C816),
            ByteAnnotation.Default.WithArch(CpuArch.Spc700));
    }
}
