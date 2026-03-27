using Xunit;
using Mesen.Annotation;
using Mesen.Annotation.Diz;

namespace Mesen.Tests.Annotation.Diz;

public class DizProjectImporterTests
{
    // ── Minimal valid XML template ─────────────────────────────────────────────

    private const string SysNs  = "https://extendedxmlserializer.github.io/system";
    private const string ClrNs  = "clr-namespace:Diz.Core.serialization.xml_serializer;assembly=Diz.Core";

    /// <summary>
    /// Builds a minimal valid .dizraw XML string.
    /// RomBytes defaults to a single Unreached byte ("U").
    /// </summary>
    private static string MakeXml(
        string watermark    = "DiztinGUIsh",
        string saveVersion  = "104",
        string gameName     = "TEST GAME",
        string checksum     = "0",
        string mapMode      = "LoRom",
        string speed        = "SlowRom",
        string romBytesBody = "\nversion:201\nU",
        string commentsXml  = "",
        string labelsXml    = "") =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ProjectXmlSerializer-Root SaveVersion="{saveVersion}" Watermark="{watermark}" xmlns:sys="{SysNs}">
          <Project InternalRomGameName="{gameName}" InternalCheckSum="{checksum}">
            <Data RomMapMode="{mapMode}" RomSpeed="{speed}">
              <RomBytes>{romBytesBody}</RomBytes>
              {commentsXml}
              {labelsXml}
            </Data>
          </Project>
        </ProjectXmlSerializer-Root>
        """;

    // ── Validation — Watermark ────────────────────────────────────────────────

    [Fact]
    public void Import_MissingWatermark_Throws()
    {
        var xml = MakeXml(watermark: "");
        var ex = Assert.Throws<InvalidDataException>(() => DizProjectImporter.Import(xml));
        Assert.Contains("Watermark", ex.Message);
    }

    [Fact]
    public void Import_WrongWatermark_Throws()
    {
        var xml = MakeXml(watermark: "NotDiz");
        var ex = Assert.Throws<InvalidDataException>(() => DizProjectImporter.Import(xml));
        Assert.Contains("Watermark", ex.Message);
    }

    // ── Validation — SaveVersion ──────────────────────────────────────────────

    [Fact]
    public void Import_SaveVersionTooLow_Throws()
    {
        var xml = MakeXml(saveVersion: "99");
        var ex = Assert.Throws<InvalidDataException>(() => DizProjectImporter.Import(xml));
        Assert.Contains("SaveVersion", ex.Message);
    }

    [Fact]
    public void Import_SaveVersionTooHigh_Throws()
    {
        var xml = MakeXml(saveVersion: "105");
        var ex = Assert.Throws<InvalidDataException>(() => DizProjectImporter.Import(xml));
        Assert.Contains("SaveVersion", ex.Message);
    }

    [Theory]
    [InlineData("100")]
    [InlineData("101")]
    [InlineData("102")]
    [InlineData("103")]
    [InlineData("104")]
    public void Import_SaveVersionInRange_Succeeds(string version)
    {
        var xml   = MakeXml(saveVersion: version);
        var store = DizProjectImporter.Import(xml);
        Assert.Equal(int.Parse(version), store.SaveVersion);
    }

    // ── Header fields ─────────────────────────────────────────────────────────

    [Fact]
    public void Import_GameName_TrimmedAndStored()
    {
        var xml   = MakeXml(gameName: "  MY GAME  ");
        var store = DizProjectImporter.Import(xml);
        Assert.Equal("MY GAME", store.RomGameName);
    }

    [Fact]
    public void Import_Checksum_ParsedCorrectly()
    {
        var xml   = MakeXml(checksum: "4111207155");
        var store = DizProjectImporter.Import(xml);
        Assert.Equal(4111207155u, store.RomChecksum);
    }

    [Theory]
    [InlineData("LoRom",    RomMapMode.LoRom)]
    [InlineData("HiRom",    RomMapMode.HiRom)]
    [InlineData("ExHiRom",  RomMapMode.ExHiRom)]
    public void Import_MapMode_ParsedCorrectly(string raw, RomMapMode expected)
    {
        var xml   = MakeXml(mapMode: raw);
        var store = DizProjectImporter.Import(xml);
        Assert.Equal(expected, store.MapMode);
    }

    [Theory]
    [InlineData("SlowRom", RomSpeed.SlowRom)]
    [InlineData("FastRom", RomSpeed.FastRom)]
    public void Import_Speed_ParsedCorrectly(string raw, RomSpeed expected)
    {
        var xml   = MakeXml(speed: raw);
        var store = DizProjectImporter.Import(xml);
        Assert.Equal(expected, store.Speed);
    }

    // ── RomBytes ──────────────────────────────────────────────────────────────

    [Fact]
    public void Import_SingleUnreachedByte_Decoded()
    {
        var xml   = MakeXml(romBytesBody: "\nversion:201\nU");
        var store = DizProjectImporter.Import(xml);
        Assert.Single(store.Bytes);
        Assert.Equal(ByteType.Unreached, store.Bytes[0].Type);
    }

    [Fact]
    public void Import_OpcodeFollowedByOperand_Decoded()
    {
        var xml   = MakeXml(romBytesBody: "\nversion:201\n+\n.");
        var store = DizProjectImporter.Import(xml);
        Assert.Equal(2, store.Bytes.Length);
        Assert.Equal(ByteType.Opcode,   store.Bytes[0].Type);
        Assert.Equal(ByteType.Operand,  store.Bytes[1].Type);
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    [Fact]
    public void Import_NoCommentsElement_EmptyDictionary()
    {
        var xml   = MakeXml(commentsXml: "");
        var store = DizProjectImporter.Import(xml);
        Assert.Empty(store.Comments);
    }

    [Fact]
    public void Import_SingleComment_Parsed()
    {
        var commentsXml = $"""
            <Comments xmlns:sys="{SysNs}">
              <sys:Item Key="32768" Value="entry point" />
            </Comments>
            """;
        var xml   = MakeXml(commentsXml: commentsXml);
        var store = DizProjectImporter.Import(xml);
        Assert.Single(store.Comments);
        Assert.Equal("entry point", store.Comments[32768]);
    }

    [Fact]
    public void Import_MultipleComments_AllParsed()
    {
        var commentsXml = $"""
            <Comments xmlns:sys="{SysNs}">
              <sys:Item Key="32768" Value="first" />
              <sys:Item Key="32769" Value="second" />
            </Comments>
            """;
        var xml   = MakeXml(commentsXml: commentsXml);
        var store = DizProjectImporter.Import(xml);
        Assert.Equal(2, store.Comments.Count);
        Assert.Equal("first",  store.Comments[32768]);
        Assert.Equal("second", store.Comments[32769]);
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    [Fact]
    public void Import_NoLabelsElement_EmptyDictionaries()
    {
        var xml   = MakeXml(labelsXml: "");
        var store = DizProjectImporter.Import(xml);
        Assert.Empty(store.Labels);
        Assert.Empty(store.LabelComments);
    }

    [Fact]
    public void Import_SingleLabel_NameAndCommentParsed()
    {
        var labelsXml = $"""
            <Labels xmlns:sys="{SysNs}" xmlns="{ClrNs}">
              <sys:Item Key="32768">
                <Value Name="Start" Comment="main entry" />
              </sys:Item>
            </Labels>
            """;
        var xml   = MakeXml(labelsXml: labelsXml);
        var store = DizProjectImporter.Import(xml);
        Assert.Equal("Start",       store.Labels[32768]);
        Assert.Equal("main entry",  store.LabelComments[32768]);
    }

    [Fact]
    public void Import_LabelWithEmptyComment_NotInLabelComments()
    {
        var labelsXml = $"""
            <Labels xmlns:sys="{SysNs}" xmlns="{ClrNs}">
              <sys:Item Key="32768">
                <Value Name="Start" Comment="" />
              </sys:Item>
            </Labels>
            """;
        var xml   = MakeXml(labelsXml: labelsXml);
        var store = DizProjectImporter.Import(xml);
        Assert.Equal("Start", store.Labels[32768]);
        Assert.DoesNotContain(32768, store.LabelComments.Keys);
    }

    [Fact]
    public void Import_MultipleLabels_AllParsed()
    {
        var labelsXml = $"""
            <Labels xmlns:sys="{SysNs}" xmlns="{ClrNs}">
              <sys:Item Key="32768">
                <Value Name="LabelA" Comment="" />
              </sys:Item>
              <sys:Item Key="32769">
                <Value Name="LabelB" Comment="note" />
              </sys:Item>
            </Labels>
            """;
        var xml   = MakeXml(labelsXml: labelsXml);
        var store = DizProjectImporter.Import(xml);
        Assert.Equal(2,       store.Labels.Count);
        Assert.Equal("LabelA", store.Labels[32768]);
        Assert.Equal("LabelB", store.Labels[32769]);
        Assert.Single(store.LabelComments);
        Assert.Equal("note",   store.LabelComments[32769]);
    }
}
