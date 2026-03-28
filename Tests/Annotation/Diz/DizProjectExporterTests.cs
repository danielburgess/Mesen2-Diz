using Xunit;
using Mesen.Annotation;
using Mesen.Annotation.Diz;

namespace Mesen.Tests.Annotation.Diz;

public class DizProjectExporterTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RomAnnotationStore MinimalStore(
        string gameName  = "MY GAME",
        uint checksum    = 0xDEADBEEFu,
        RomMapMode map   = RomMapMode.LoRom,
        RomSpeed speed   = RomSpeed.SlowRom,
        ByteAnnotation[]? bytes = null,
        Dictionary<int, string>? labels        = null,
        Dictionary<int, string>? labelComments = null,
        Dictionary<int, string>? comments      = null) =>
        new()
        {
            RomGameName   = gameName,
            RomChecksum   = checksum,
            MapMode       = map,
            Speed         = speed,
            SaveVersion   = 104,
            Bytes         = bytes ?? [new ByteAnnotation { Type = ByteType.Unreached }],
            Labels        = labels        ?? new Dictionary<int, string>(),
            LabelComments = labelComments ?? new Dictionary<int, string>(),
            Comments      = comments      ?? new Dictionary<int, string>(),
        };

    private static RomAnnotationStore RoundTrip(RomAnnotationStore store)
    {
        var xml = DizProjectExporter.Export(store);
        return DizProjectImporter.Import(xml);
    }

    // ── SaveVersion validation ────────────────────────────────────────────────

    [Fact]
    public void Export_SaveVersionTooLow_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DizProjectExporter.Export(MinimalStore(), saveVersion: 99));
    }

    [Fact]
    public void Export_SaveVersionTooHigh_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DizProjectExporter.Export(MinimalStore(), saveVersion: 105));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(104)]
    public void Export_SaveVersionInRange_Succeeds(int version)
    {
        var xml = DizProjectExporter.Export(MinimalStore(), saveVersion: version);
        Assert.Contains($"SaveVersion=\"{version}\"", xml);
    }

    // ── XML structure ─────────────────────────────────────────────────────────

    [Fact]
    public void Export_ContainsWatermark()
    {
        var xml = DizProjectExporter.Export(MinimalStore());
        Assert.Contains("Watermark=\"DiztinGUIsh\"", xml);
    }

    [Fact]
    public void Export_ContainsSysNamespace()
    {
        var xml = DizProjectExporter.Export(MinimalStore());
        Assert.Contains("extendedxmlserializer.github.io/system", xml);
    }

    [Fact]
    public void Export_RootHasDefaultClrNamespace()
    {
        // DiztinGUIsh's ExtendedXmlSerializer locates root type via
        // xmlns="clr-namespace:Diz.Core.serialization.xml_serializer;assembly=Diz.Core"
        var xml = DizProjectExporter.Export(MinimalStore());
        Assert.Contains("xmlns=\"clr-namespace:Diz.Core.serialization.xml_serializer;assembly=Diz.Core\"", xml);
    }

    [Fact]
    public void Export_HasXmlDeclaration()
    {
        var xml = DizProjectExporter.Export(MinimalStore());
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
    }

    [Fact]
    public void Export_HasExtra1Extra2Attributes()
    {
        var xml = DizProjectExporter.Export(MinimalStore());
        Assert.Contains("Extra1=\"\"", xml);
        Assert.Contains("Extra2=\"\"", xml);
    }

    [Fact]
    public void Export_IsValidXml()
    {
        var xml = DizProjectExporter.Export(MinimalStore());
        // Should not throw.
        _ = System.Xml.Linq.XDocument.Parse(xml);
    }

    // ── Header fields ─────────────────────────────────────────────────────────

    [Fact]
    public void Export_GameName_Written()
    {
        var xml = DizProjectExporter.Export(MinimalStore(gameName: "ZAMN"));
        Assert.Contains("ZAMN", xml);
    }

    [Fact]
    public void Export_Checksum_WrittenAsDecimal()
    {
        // 0xDEADBEEF == 3735928559; must NOT appear as hex in the XML.
        var xml = DizProjectExporter.Export(MinimalStore(checksum: 0xDEADBEEFu));
        Assert.Contains("3735928559", xml);
        Assert.DoesNotContain("DEADBEEF", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RomMapMode.LoRom,   "LoRom")]
    [InlineData(RomMapMode.HiRom,   "HiRom")]
    [InlineData(RomMapMode.ExHiRom, "ExHiRom")]
    public void Export_MapMode_Written(RomMapMode mode, string expected)
    {
        var xml = DizProjectExporter.Export(MinimalStore(map: mode));
        Assert.Contains($"RomMapMode=\"{expected}\"", xml);
    }

    [Theory]
    [InlineData(RomSpeed.SlowRom, "SlowRom")]
    [InlineData(RomSpeed.FastRom, "FastRom")]
    public void Export_Speed_Written(RomSpeed speed, string expected)
    {
        var xml = DizProjectExporter.Export(MinimalStore(speed: speed));
        Assert.Contains($"RomSpeed=\"{expected}\"", xml);
    }

    // ── Empty collections ─────────────────────────────────────────────────────

    [Fact]
    public void Export_EmptyComments_ElementOmitted()
    {
        var xml = DizProjectExporter.Export(MinimalStore());
        Assert.DoesNotContain("<Comments", xml);
    }

    [Fact]
    public void Export_EmptyLabels_ElementOmitted()
    {
        var xml = DizProjectExporter.Export(MinimalStore());
        Assert.DoesNotContain("Labels", xml);
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    [Fact]
    public void Export_SingleComment_Written()
    {
        var store = MinimalStore(comments: new() { [0x8000] = "entry point" });
        var xml   = DizProjectExporter.Export(store);
        Assert.Contains("Key=\"32768\"",        xml);
        Assert.Contains("Value=\"entry point\"", xml);
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    [Fact]
    public void Export_SingleLabel_NameAndComment_Written()
    {
        var store = MinimalStore(
            labels:        new() { [0x8000] = "Start" },
            labelComments: new() { [0x8000] = "main entry" });
        var xml = DizProjectExporter.Export(store);
        Assert.Contains("Name=\"Start\"",       xml);
        Assert.Contains("Comment=\"main entry\"", xml);
    }

    [Fact]
    public void Export_LabelWithoutComment_WritesEmptyCommentAttr()
    {
        var store = MinimalStore(labels: new() { [0x8000] = "Start" });
        var xml   = DizProjectExporter.Export(store);
        Assert.Contains("Name=\"Start\"", xml);
        Assert.Contains("Comment=\"\"",   xml);
    }

    // ── RomBytes / compression ────────────────────────────────────────────────

    [Fact]
    public void Export_SingleByte_Opcode_Written()
    {
        var store = MinimalStore(bytes: [new ByteAnnotation { Type = ByteType.Opcode }]);
        var xml   = DizProjectExporter.Export(store);
        // The RomBytes section must contain a '+' line (Opcode flag char).
        Assert.Contains("+", xml);
    }

    [Fact]
    public void Export_RleCompression_Applied()
    {
        // 8 identical unreached bytes should produce "r 8 U" in the RomBytes section.
        var bytes = Enumerable.Repeat(new ByteAnnotation { Type = ByteType.Unreached }, 8)
                              .ToArray();
        var store = MinimalStore(bytes: bytes);
        var xml   = DizProjectExporter.Export(store);
        Assert.Contains("r 8 U", xml);
    }

    [Fact]
    public void Export_SmallByteArray_NoRle()
    {
        // < 8 bytes: RLE should NOT be applied.
        var bytes = Enumerable.Repeat(new ByteAnnotation { Type = ByteType.Unreached }, 3)
                              .ToArray();
        var store = MinimalStore(bytes: bytes);
        var xml   = DizProjectExporter.Export(store);
        Assert.DoesNotContain("r 3 U", xml);
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ScalarFields_Preserved()
    {
        var original = MinimalStore(
            gameName: "ROUND TRIP",
            checksum: 0xCAFEBABEu,
            map:      RomMapMode.HiRom,
            speed:    RomSpeed.FastRom);

        var restored = RoundTrip(original);

        Assert.Equal(original.RomGameName, restored.RomGameName);
        Assert.Equal(original.RomChecksum, restored.RomChecksum);
        Assert.Equal(original.MapMode,     restored.MapMode);
        Assert.Equal(original.Speed,       restored.Speed);
    }

    [Fact]
    public void RoundTrip_Bytes_Preserved()
    {
        var bytes = new ByteAnnotation[]
        {
            new() { Type = ByteType.Opcode,  MFlag = true,  XFlag = true  },
            new() { Type = ByteType.Operand                               },
            new() { Type = ByteType.Unreached                             },
        };
        var store = MinimalStore(bytes: bytes);

        var restored = RoundTrip(store);

        Assert.Equal(3, restored.Bytes.Length);
        Assert.Equal(ByteType.Opcode,    restored.Bytes[0].Type);
        Assert.True(restored.Bytes[0].MFlag);
        Assert.True(restored.Bytes[0].XFlag);
        Assert.Equal(ByteType.Operand,   restored.Bytes[1].Type);
        Assert.Equal(ByteType.Unreached, restored.Bytes[2].Type);
    }

    [Fact]
    public void RoundTrip_Comments_Preserved()
    {
        var store    = MinimalStore(comments: new() { [0x8000] = "entry", [0x8001] = "next" });
        var restored = RoundTrip(store);

        Assert.Equal(2,       restored.Comments.Count);
        Assert.Equal("entry", restored.Comments[0x8000]);
        Assert.Equal("next",  restored.Comments[0x8001]);
    }

    [Fact]
    public void RoundTrip_Labels_Preserved()
    {
        var store = MinimalStore(
            labels:        new() { [0x8000] = "Start", [0x8010] = "Sub" },
            labelComments: new() { [0x8000] = "entry point" });

        var restored = RoundTrip(store);

        Assert.Equal(2,            restored.Labels.Count);
        Assert.Equal("Start",      restored.Labels[0x8000]);
        Assert.Equal("Sub",        restored.Labels[0x8010]);
        Assert.Single(restored.LabelComments);
        Assert.Equal("entry point", restored.LabelComments[0x8000]);
    }
}
