using Xunit;
using Mesen.Annotation;
using Mesen.Annotation.Diz;

namespace Mesen.Tests.Annotation.Diz;

/// <summary>
/// Known encode/decode vectors are derived directly from DiztinGUIsh's
/// SnesSampleRomDataFactory sample data — grounding correctness in the
/// original codebase rather than in assumptions.
/// </summary>
public class DizByteLineCodecTests
{
    // ── Known encode vectors ──────────────────────────────────────────────────
    //
    // How each was derived:
    //   pos[0] = flag char (see CharToType table)
    //   pos[1] = DizFake64.Encode( (XFlag?1:0)<<2 | (MFlag?1:0)<<3 | (byte)Flow<<4 )
    //   pos[2-3] = DataBank as 2 uppercase hex digits
    //   pos[4-7] = DirectPage as 4 uppercase hex digits
    //   pos[8]   = (byte)Arch as 1 hex digit
    //   trailing '0' characters stripped from the right

    [Theory]
    // Annotation                                                  Expected
    [InlineData(ByteType.Unreached, false, false, 0x00, 0x0000, InOutPoint.None,     CpuArch.Cpu65C816, "U")]
    [InlineData(ByteType.Opcode,    true,  true,  0x00, 0x0000, InOutPoint.InPoint,  CpuArch.Cpu65C816, "+H")]
    [InlineData(ByteType.Opcode,    true,  true,  0x00, 0x0000, InOutPoint.EndPoint, CpuArch.Cpu65C816, "+T")]
    [InlineData(ByteType.Opcode,    false, false, 0x00, 0x0000, InOutPoint.None,     CpuArch.Cpu65C816, "+")]
    [InlineData(ByteType.Operand,   false, false, 0x00, 0x0000, InOutPoint.None,     CpuArch.Cpu65C816, ".")]
    [InlineData(ByteType.Opcode,    false, false, 0x80, 0x2100, InOutPoint.None,     CpuArch.Cpu65C816, "+08021")]
    [InlineData(ByteType.Data16,    false, false, 0x80, 0x2100, InOutPoint.ReadPoint,CpuArch.Cpu65C816, "Bg8021")]
    [InlineData(ByteType.Pointer16, false, false, 0x80, 0x2100, InOutPoint.ReadPoint,CpuArch.Cpu65C816, "Eg8021")]
    [InlineData(ByteType.Graphics,  false, false, 0x00, 0x0000, InOutPoint.None,     CpuArch.Cpu65C816, "G")]
    [InlineData(ByteType.Music,     false, false, 0x00, 0x0000, InOutPoint.None,     CpuArch.Cpu65C816, "M")]
    [InlineData(ByteType.Text,      false, false, 0x00, 0x0000, InOutPoint.None,     CpuArch.Cpu65C816, "T")]
    [InlineData(ByteType.Empty,     false, false, 0x00, 0x0000, InOutPoint.None,     CpuArch.Cpu65C816, "X")]
    public void Encode_KnownVector(
        ByteType type, bool mFlag, bool xFlag, byte db, ushort dp,
        InOutPoint flow, CpuArch arch, string expected)
    {
        var annotation = new ByteAnnotation
        {
            Type = type, MFlag = mFlag, XFlag = xFlag,
            DataBank = db, DirectPage = dp, Flow = flow, Arch = arch,
        };
        Assert.Equal(expected, DizByteLineCodec.Encode(annotation));
    }

    // ── Known decode vectors ──────────────────────────────────────────────────

    [Theory]
    [InlineData("U",       ByteType.Unreached, false, false, 0x00, 0x0000, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData("+H",      ByteType.Opcode,    true,  true,  0x00, 0x0000, InOutPoint.InPoint,   CpuArch.Cpu65C816)]
    [InlineData("+T",      ByteType.Opcode,    true,  true,  0x00, 0x0000, InOutPoint.EndPoint,  CpuArch.Cpu65C816)]
    [InlineData("+",       ByteType.Opcode,    false, false, 0x00, 0x0000, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData(".",       ByteType.Operand,   false, false, 0x00, 0x0000, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData("+08021",  ByteType.Opcode,    false, false, 0x80, 0x2100, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData("Bg8021",  ByteType.Data16,    false, false, 0x80, 0x2100, InOutPoint.ReadPoint, CpuArch.Cpu65C816)]
    [InlineData("Eg8021",  ByteType.Pointer16, false, false, 0x80, 0x2100, InOutPoint.ReadPoint, CpuArch.Cpu65C816)]
    [InlineData("G",       ByteType.Graphics,  false, false, 0x00, 0x0000, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData("M",       ByteType.Music,     false, false, 0x00, 0x0000, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData("T",       ByteType.Text,      false, false, 0x00, 0x0000, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData("X",       ByteType.Empty,     false, false, 0x00, 0x0000, InOutPoint.None,      CpuArch.Cpu65C816)]
    public void Decode_KnownVector(
        string line,
        ByteType expectedType, bool expectedM, bool expectedX,
        byte expectedDb, ushort expectedDp,
        InOutPoint expectedFlow, CpuArch expectedArch)
    {
        var a = DizByteLineCodec.Decode(line);
        Assert.Equal(expectedType, a.Type);
        Assert.Equal(expectedM,    a.MFlag);
        Assert.Equal(expectedX,    a.XFlag);
        Assert.Equal(expectedDb,   a.DataBank);
        Assert.Equal(expectedDp,   a.DirectPage);
        Assert.Equal(expectedFlow, a.Flow);
        Assert.Equal(expectedArch, a.Arch);
    }

    // ── Round-trip: encode → decode ───────────────────────────────────────────

    [Theory]
    [InlineData(ByteType.Unreached, false, false, 0x00, 0x0000, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData(ByteType.Opcode,    true,  true,  0x00, 0x0000, InOutPoint.InPoint,   CpuArch.Cpu65C816)]
    [InlineData(ByteType.Opcode,    true,  true,  0x00, 0x0000, InOutPoint.EndPoint,  CpuArch.Cpu65C816)]
    [InlineData(ByteType.Opcode,    false, false, 0x80, 0x2100, InOutPoint.None,      CpuArch.Cpu65C816)]
    [InlineData(ByteType.Data16,    false, false, 0x80, 0x2100, InOutPoint.ReadPoint, CpuArch.Cpu65C816)]
    [InlineData(ByteType.Data32,    true,  false, 0x7E, 0xFFFF, InOutPoint.InPoint | InOutPoint.OutPoint, CpuArch.Spc700)]
    public void RoundTrip_EncodeThenDecode(
        ByteType type, bool mFlag, bool xFlag, byte db, ushort dp,
        InOutPoint flow, CpuArch arch)
    {
        var original = new ByteAnnotation
        {
            Type = type, MFlag = mFlag, XFlag = xFlag,
            DataBank = db, DirectPage = dp, Flow = flow, Arch = arch,
        };
        var encoded = DizByteLineCodec.Encode(original);
        var decoded = DizByteLineCodec.Decode(encoded);
        Assert.Equal(original, decoded);
    }

    // ── Round-trip: decode → encode ───────────────────────────────────────────

    [Theory]
    [InlineData("U")]
    [InlineData("+H")]
    [InlineData("+T")]
    [InlineData("+")]
    [InlineData(".")]
    [InlineData("+08021")]
    [InlineData("Bg8021")]
    [InlineData("Eg8021")]
    public void RoundTrip_DecodeThenEncode(string line)
    {
        var decoded = DizByteLineCodec.Decode(line);
        var reEncoded = DizByteLineCodec.Encode(decoded);
        Assert.Equal(line, reEncoded);
    }

    // ── Trailing zero stripping ───────────────────────────────────────────────

    [Fact]
    public void Encode_DefaultAnnotation_ProducesOnlyFlagChar()
    {
        // Every field is default/zero — only the flag char should survive.
        var encoded = DizByteLineCodec.Encode(new ByteAnnotation { Type = ByteType.Opcode });
        Assert.Equal("+", encoded);
        Assert.DoesNotContain("0", encoded.Substring(1)); // no trailing zeros
    }

    // ── Short line padding ────────────────────────────────────────────────────

    [Fact]
    public void Decode_ShortLine_PadsWithZeros()
    {
        // "+" is just the flag char; all other fields must decode as zero/default.
        var a = DizByteLineCodec.Decode("+");
        Assert.Equal(ByteType.Opcode,     a.Type);
        Assert.False(a.MFlag);
        Assert.False(a.XFlag);
        Assert.Equal((byte)0,             a.DataBank);
        Assert.Equal((ushort)0,           a.DirectPage);
        Assert.Equal(InOutPoint.None,     a.Flow);
        Assert.Equal(CpuArch.Cpu65C816,   a.Arch);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Decode_UnknownFlagChar_Throws()
    {
        Assert.Throws<ArgumentException>(() => DizByteLineCodec.Decode("Z"));
    }

    [Fact]
    public void Encode_UnknownByteType_Throws()
    {
        var corrupt = new ByteAnnotation { Type = (ByteType)0xFF };
        Assert.Throws<ArgumentException>(() => DizByteLineCodec.Encode(corrupt));
    }
}
