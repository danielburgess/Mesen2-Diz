using Xunit;
using Mesen.Annotation;
using Mesen.Annotation.Diz;

namespace Mesen.Tests.Annotation.Diz;

public class DizRomBytesSectionTests
{
    // Helper: build a valid section string from options + data lines.
    // Mirrors exactly what DiztinGUIsh's RomBytesSerializer.Write() produces.
    private static string Section(string options, params string[] dataLines) =>
        $"\n{options}\n" + string.Join("\n", dataLines) + "\n";

    // ── 1. Minimal uncompressed section ──────────────────────────────────────

    [Fact]
    public void Decode_Uncompressed_ThreeBytes()
    {
        var content = Section("version:201", "U", "+H", ".");
        var result = DizRomBytesDecoder.Decode(content);

        Assert.Equal(3, result.Length);
        Assert.Equal(ByteType.Unreached, result[0].Type);
        Assert.Equal(ByteType.Opcode,   result[1].Type);
        Assert.Equal(ByteType.Operand,  result[2].Type);
        // spot-check the Opcode row has the M/X/Flow from "+H"
        Assert.True(result[1].MFlag);
        Assert.True(result[1].XFlag);
        Assert.Equal(InOutPoint.InPoint, result[1].Flow);
    }

    // ── 2. Version 200 accepted (trivial migration) ───────────────────────────

    [Fact]
    public void Decode_Version200_Accepted()
    {
        var content = Section("version:200", "U");
        var result = DizRomBytesDecoder.Decode(content);
        Assert.Single(result);
        Assert.Equal(ByteType.Unreached, result[0].Type);
    }

    // ── 3. Too-new version rejects ────────────────────────────────────────────

    [Fact]
    public void Decode_TooNewVersion_Throws()
    {
        var content = Section("version:999", "U");
        Assert.Throws<InvalidDataException>(() => DizRomBytesDecoder.Decode(content));
    }

    // ── 4. Too-old version rejects ────────────────────────────────────────────

    [Fact]
    public void Decode_TooOldVersion_Throws()
    {
        var content = Section("version:100", "U");
        Assert.Throws<InvalidDataException>(() => DizRomBytesDecoder.Decode(content));
    }

    // ── 5. Missing version tag rejects ───────────────────────────────────────

    [Fact]
    public void Decode_MissingVersion_Throws()
    {
        var content = Section("compress_groupblocks", "U");
        Assert.Throws<InvalidDataException>(() => DizRomBytesDecoder.Decode(content));
    }

    // ── 6. RLE decompression ─────────────────────────────────────────────────

    [Fact]
    public void Decode_Rle_ExpandsCorrectly()
    {
        var content = Section("version:201,compress_groupblocks", "r 4 U");
        var result = DizRomBytesDecoder.Decode(content);

        Assert.Equal(4, result.Length);
        Assert.All(result, r => Assert.Equal(ByteType.Unreached, r.Type));
    }

    [Fact]
    public void Decode_Rle_MixedWithLiteralLines()
    {
        var content = Section("version:201,compress_groupblocks", "+", "r 3 .", "G");
        var result = DizRomBytesDecoder.Decode(content);

        Assert.Equal(5, result.Length);
        Assert.Equal(ByteType.Opcode,   result[0].Type);
        Assert.Equal(ByteType.Operand,  result[1].Type);
        Assert.Equal(ByteType.Operand,  result[2].Type);
        Assert.Equal(ByteType.Operand,  result[3].Type);
        Assert.Equal(ByteType.Graphics, result[4].Type);
    }

    [Fact]
    public void Decode_Rle_CountOfOne_Valid()
    {
        // RLE threshold only applies during ENCODE; decoder must accept any count >= 1
        var content = Section("version:201,compress_groupblocks", "r 1 +");
        var result = DizRomBytesDecoder.Decode(content);
        Assert.Single(result);
        Assert.Equal(ByteType.Opcode, result[0].Type);
    }

    [Fact]
    public void Decode_Rle_MalformedEntry_Throws()
    {
        var content = Section("version:201,compress_groupblocks", "r 4");
        Assert.Throws<InvalidDataException>(() => DizRomBytesDecoder.Decode(content));
    }

    // ── 7. Substitution decompression ────────────────────────────────────────
    //
    // "Eg00001E" encodes: Pointer16, ReadPoint, DB=0x00, DP=0x001E
    // After compress_table_1 encoding:  "0001E" → "ZQ"  →  "Eg0ZQ"
    // Decoding must reverse this to recover "Eg00001E" before per-line parsing.

    [Fact]
    public void Decode_Table1_RevertsSubstitution()
    {
        var content = Section("version:201,compress_table_1", "Eg0ZQ");
        var result = DizRomBytesDecoder.Decode(content);

        Assert.Single(result);
        Assert.Equal(ByteType.Pointer16,    result[0].Type);
        Assert.Equal(InOutPoint.ReadPoint,  result[0].Flow);
        Assert.Equal((byte)0x00,            result[0].DataBank);
        Assert.Equal((ushort)0x001E,        result[0].DirectPage);
    }

    // ── 8. Both compressions together ────────────────────────────────────────
    //
    // Encode order:  data  → RLE  → Table1
    // Decode order:  Table1 reverse  → RLE expand  → per-line parse
    //
    // "r 4 Eg0ZQ": Table1 decode → "r 4 Eg00001E", then RLE expand → 4 copies of "Eg00001E"

    [Fact]
    public void Decode_BothCompressions_CorrectOrder()
    {
        var content = Section("version:201,compress_table_1,compress_groupblocks", "r 4 Eg0ZQ");
        var result = DizRomBytesDecoder.Decode(content);

        Assert.Equal(4, result.Length);
        Assert.All(result, r =>
        {
            Assert.Equal(ByteType.Pointer16,   r.Type);
            Assert.Equal(InOutPoint.ReadPoint, r.Flow);
            Assert.Equal((byte)0x00,           r.DataBank);
            Assert.Equal((ushort)0x001E,       r.DirectPage);
        });
    }

    // ── 9. Inline comments stripped ──────────────────────────────────────────

    [Fact]
    public void Decode_InlineComment_Stripped()
    {
        // Diz inserts ";pos=XXXXXX" comments as merge anchors — must be ignored
        var content = Section("version:201",
            "U",
            ";pos=000800",   // pure comment line
            "+H;pos=000801", // inline comment on a data line
            ".");

        var result = DizRomBytesDecoder.Decode(content);

        Assert.Equal(3, result.Length);
        Assert.Equal(ByteType.Unreached, result[0].Type);
        Assert.Equal(ByteType.Opcode,   result[1].Type);
        Assert.Equal(ByteType.Operand,  result[2].Type);
    }

    // ── 10. Blank lines ignored ───────────────────────────────────────────────

    [Fact]
    public void Decode_BlankLines_Ignored()
    {
        // inject extra blank lines into the data body
        var content = "\nversion:201\nU\n\n+\n\n.\n";
        var result = DizRomBytesDecoder.Decode(content);

        Assert.Equal(3, result.Length);
    }

    // ── 11. Exact byte count ──────────────────────────────────────────────────

    [Fact]
    public void Decode_FiveLines_ReturnsFiveAnnotations()
    {
        var content = Section("version:201", "U", "+", ".", "G", "M");
        var result = DizRomBytesDecoder.Decode(content);
        Assert.Equal(5, result.Length);
    }
}
