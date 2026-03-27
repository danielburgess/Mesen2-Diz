using Xunit;
using Mesen.Annotation;
using Mesen.Annotation.Diz;

namespace Mesen.Tests.Annotation;

public class DizToMesenAdapterTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RomAnnotationStore MakeStore(
        uint checksum               = 0xDEADBEEFu,
        RomMapMode mapMode          = RomMapMode.LoRom,
        ByteAnnotation[]? bytes     = null,
        Dictionary<int, string>? labels        = null,
        Dictionary<int, string>? labelComments = null,
        Dictionary<int, string>? comments      = null) =>
        new()
        {
            RomGameName   = "TEST",
            RomChecksum   = checksum,
            MapMode       = mapMode,
            Speed         = RomSpeed.SlowRom,
            SaveVersion   = 104,
            Bytes         = bytes ?? [],
            Labels        = labels        ?? new Dictionary<int, string>(),
            LabelComments = labelComments ?? new Dictionary<int, string>(),
            Comments      = comments      ?? new Dictionary<int, string>(),
        };

    // ══════════════════════════════════════════════════════════════════════════
    // CDL
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ToCdl_Header_IsCDLv2Magic()
    {
        var store  = MakeStore(bytes: []);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal((byte)'C', result[0]);
        Assert.Equal((byte)'D', result[1]);
        Assert.Equal((byte)'L', result[2]);
        Assert.Equal((byte)'v', result[3]);
        Assert.Equal((byte)'2', result[4]);
    }

    [Fact]
    public void ToCdl_Header_ContainsCrc32LittleEndian()
    {
        // checksum 0x01020304 → bytes [04, 03, 02, 01] at offsets 5-8
        var store  = MakeStore(checksum: 0x01020304u, bytes: []);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(0x04, result[5]);
        Assert.Equal(0x03, result[6]);
        Assert.Equal(0x02, result[7]);
        Assert.Equal(0x01, result[8]);
    }

    [Fact]
    public void ToCdl_TotalLength_IsHeaderPlusByteCount()
    {
        var bytes  = new ByteAnnotation[42];
        var store  = MakeStore(bytes: bytes);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(9 + 42, result.Length);
    }

    [Fact]
    public void ToCdl_Opcode_IsCdlCode()
    {
        var bytes  = new[] { new ByteAnnotation { Type = ByteType.Opcode } };
        var store  = MakeStore(bytes: bytes);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(0x01, result[9]); // CdlFlags.Code
    }

    [Fact]
    public void ToCdl_OpcodeWithInPoint_IsCodeAndSubEntryPoint()
    {
        var bytes  = new[] { new ByteAnnotation { Type = ByteType.Opcode, Flow = InOutPoint.InPoint } };
        var store  = MakeStore(bytes: bytes);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(0x01 | 0x08, result[9]); // Code | SubEntryPoint
    }

    [Fact]
    public void ToCdl_OpcodeWithoutInPoint_NoSubEntryPoint()
    {
        var bytes  = new[] { new ByteAnnotation { Type = ByteType.Opcode, Flow = InOutPoint.OutPoint } };
        var store  = MakeStore(bytes: bytes);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(0x01, result[9]); // Code only, no SubEntryPoint
    }

    [Fact]
    public void ToCdl_Operand_IsCdlCode()
    {
        var bytes  = new[] { new ByteAnnotation { Type = ByteType.Operand } };
        var store  = MakeStore(bytes: bytes);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(0x01, result[9]);
    }

    [Theory]
    [InlineData(ByteType.Data8)]
    [InlineData(ByteType.Data16)]
    [InlineData(ByteType.Data24)]
    [InlineData(ByteType.Data32)]
    [InlineData(ByteType.Pointer16)]
    [InlineData(ByteType.Pointer24)]
    [InlineData(ByteType.Pointer32)]
    [InlineData(ByteType.Graphics)]
    [InlineData(ByteType.Music)]
    [InlineData(ByteType.Empty)]
    [InlineData(ByteType.Text)]
    public void ToCdl_DataTypes_IsCdlData(ByteType t)
    {
        var bytes  = new[] { new ByteAnnotation { Type = t } };
        var store  = MakeStore(bytes: bytes);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(0x02, result[9]); // CdlFlags.Data
    }

    [Fact]
    public void ToCdl_Unreached_IsCdlNone()
    {
        var bytes  = new[] { new ByteAnnotation { Type = ByteType.Unreached } };
        var store  = MakeStore(bytes: bytes);
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(0x00, result[9]);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MLB
    // ══════════════════════════════════════════════════════════════════════════

    private static string[] MlbLines(string mlb) =>
        mlb.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void ToMlb_SingleLabel_ProducesCorrectLine()
    {
        // SNES 0x008000 → LoRom offset 0 → MLB address 0000
        var store = MakeStore(labels: new() { [0x008000] = "Start" });
        var mlb   = DizToMesenAdapter.ToMlbText(store);
        Assert.Contains("SnesPrgRom:0000:Start", mlb);
    }

    [Fact]
    public void ToMlb_LabelWithComment_IncludesComment()
    {
        var store = MakeStore(
            labels:        new() { [0x008000] = "Start" },
            labelComments: new() { [0x008000] = "entry point" });
        var mlb = DizToMesenAdapter.ToMlbText(store);
        Assert.Contains("SnesPrgRom:0000:Start:entry point", mlb);
    }

    [Fact]
    public void ToMlb_StandaloneComment_EmptyNameField()
    {
        // No label at this address, just a comment.
        var store = MakeStore(comments: new() { [0x008000] = "note" });
        var mlb   = DizToMesenAdapter.ToMlbText(store);
        Assert.Contains("SnesPrgRom:0000::note", mlb);
    }

    [Fact]
    public void ToMlb_CommentWithNewline_EscapedAsBackslashN()
    {
        var store = MakeStore(comments: new() { [0x008000] = "line1\nline2" });
        var mlb   = DizToMesenAdapter.ToMlbText(store);
        Assert.Contains(@"line1\nline2", mlb);
        Assert.DoesNotContain("line1\nline2", mlb); // literal newline must not be present
    }

    [Fact]
    public void ToMlb_NonRomSnesAddress_Skipped()
    {
        // 0x7E0000 is WRAM in LoRom — must be skipped.
        var store = MakeStore(
            labels:   new() { [0x7E0000] = "WramLabel" },
            comments: new() { [0x7E0000] = "wram note" });
        var mlb = DizToMesenAdapter.ToMlbText(store);
        Assert.Empty(MlbLines(mlb));
    }

    [Fact]
    public void ToMlb_PageBelow8000_Skipped_LoRom()
    {
        var store = MakeStore(labels: new() { [0x007FFF] = "Shadow" });
        var mlb   = DizToMesenAdapter.ToMlbText(store);
        Assert.Empty(MlbLines(mlb));
    }

    [Fact]
    public void ToMlb_SortedByRomOffset()
    {
        var store = MakeStore(labels: new()
        {
            [0x018000] = "Second",   // ROM offset 0x8000
            [0x008000] = "First",    // ROM offset 0x0000
        });
        var lines = MlbLines(DizToMesenAdapter.ToMlbText(store));
        Assert.Equal(2, lines.Length);
        Assert.Contains("First",  lines[0]);
        Assert.Contains("Second", lines[1]);
    }

    [Fact]
    public void ToMlb_LabelAndCommentAtSameAddress_OnlyOneLine()
    {
        // A label and a separate inline comment at the same SNES address
        // should produce a single merged MLB line, not two.
        var store = MakeStore(
            labels:   new() { [0x008000] = "Start" },
            comments: new() { [0x008000] = "entry" });
        var lines = MlbLines(DizToMesenAdapter.ToMlbText(store));
        Assert.Single(lines);
        Assert.Contains("Start", lines[0]);
    }

    [Fact]
    public void ToMlb_EmptyStore_EmptyOutput()
    {
        var store = MakeStore();
        Assert.Empty(DizToMesenAdapter.ToMlbText(store).Trim());
    }

    [Fact]
    public void ToMlb_HiRom_AddressTranslation()
    {
        // SNES 0x408000 → HiRom offset 0x8000 → hex "8000"
        var store = MakeStore(
            mapMode: RomMapMode.HiRom,
            labels:  new() { [0x408000] = "HiStart" });
        var mlb = DizToMesenAdapter.ToMlbText(store);
        Assert.Contains("SnesPrgRom:8000:HiStart", mlb);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Integration (zamn.dizraw — skipped if absent)
    // ══════════════════════════════════════════════════════════════════════════

    private const string DizRawPath = "/mnt/data/Projects/zamn_disassembly/zamn.dizraw";

    private static readonly Lazy<(bool exists, RomAnnotationStore? store, Exception? error)> _zamn =
        new(() =>
        {
            if (!File.Exists(DizRawPath))
                return (false, null, null);
            try
            {
                var xml   = File.ReadAllText(DizRawPath);
                var store = DizProjectImporter.Import(xml);
                return (true, store, null);
            }
            catch (Exception ex)
            {
                return (true, null, ex);
            }
        });

    private static RomAnnotationStore GetZamn()
    {
        var (exists, store, error) = _zamn.Value;
        if (!exists) throw new SkipException($"Test file not found: {DizRawPath}");
        if (error is not null) throw new InvalidOperationException("Import failed.", error);
        return store!;
    }

    [SkippableFact]
    public void ZamnCdl_Length_MatchesRomByteCount()
    {
        var store  = GetZamn();
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        Assert.Equal(9 + store.Bytes.Length, result.Length);
    }

    [SkippableFact]
    public void ZamnCdl_Header_ContainsZamnChecksum()
    {
        var store  = GetZamn();
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        uint crc   = (uint)(result[5] | (result[6] << 8) | (result[7] << 16) | (result[8] << 24));
        Assert.Equal(store.RomChecksum, crc);
    }

    [SkippableFact]
    public void ZamnCdl_HasCodeBytes()
    {
        var store  = GetZamn();
        var result = DizToMesenAdapter.ToCdlFileBytes(store);
        var data   = result.Skip(9).ToArray();
        Assert.Contains(data, b => (b & 0x01) != 0); // at least one Code byte
    }

    [SkippableFact]
    public void ZamnMlb_LabelCount_MatchesExpected()
    {
        // zamn has 22 labels; some may have empty names and some SNES addresses
        // may be in ROM — count only lines actually produced.
        var store = GetZamn();
        var lines = MlbLines(DizToMesenAdapter.ToMlbText(store));
        // All 22 label addresses are in bank 00-01 pages 8000+ (valid LoRom ROM).
        // Comments add additional lines. At minimum 22 label lines must be present.
        Assert.True(lines.Length >= 22,
            $"Expected at least 22 MLB lines, got {lines.Length}");
    }

    [SkippableFact]
    public void ZamnMlb_AllLinesHaveValidFormat()
    {
        var store = GetZamn();
        var mlb   = DizToMesenAdapter.ToMlbText(store);
        foreach (var line in MlbLines(mlb))
        {
            var fields = line.Split(':', 4);
            Assert.True(fields.Length is 3 or 4,
                $"Expected 3 or 4 colon-delimited fields, got: '{line}'");
            Assert.Equal("SnesPrgRom", fields[0]);
            Assert.True(uint.TryParse(fields[1], System.Globalization.NumberStyles.HexNumber,
                null, out _), $"Invalid hex address: '{fields[1]}'");
        }
    }
}
