using Xunit;
using Mesen.Annotation;
using Mesen.Annotation.Diz;

namespace Mesen.Tests.Annotation.Diz;

/// <summary>
/// Round-trip integration tests: import zamn.dizraw → export → re-import,
/// then assert every field in the re-imported store matches the original.
/// Skipped gracefully when the file is absent.
/// </summary>
public class DizProjectExporterIntegrationTests
{
    private const string DizRawPath =
        "/mnt/data/Projects/zamn_disassembly/zamn.dizraw";

    private static readonly Lazy<(bool exists, RomAnnotationStore? original, RomAnnotationStore? restored, Exception? error)> _result =
        new(() =>
        {
            if (!File.Exists(DizRawPath))
                return (false, null, null, null);
            try
            {
                var xml      = File.ReadAllText(DizRawPath);
                var original = DizProjectImporter.Import(xml);
                var exported = DizProjectExporter.Export(original);
                var restored = DizProjectImporter.Import(exported);
                return (true, original, restored, null);
            }
            catch (Exception ex)
            {
                return (true, null, null, ex);
            }
        });

    private static (RomAnnotationStore original, RomAnnotationStore restored) GetPair()
    {
        var (exists, original, restored, error) = _result.Value;
        if (!exists)
            throw new SkipException($"Test file not found: {DizRawPath}");
        if (error is not null)
            throw new InvalidOperationException("Round-trip threw unexpectedly.", error);
        return (original!, restored!);
    }

    // ── Scalar fields ─────────────────────────────────────────────────────────

    [SkippableFact]
    public void RoundTrip_GameName()
    {
        var (o, r) = GetPair();
        Assert.Equal(o.RomGameName, r.RomGameName);
    }

    [SkippableFact]
    public void RoundTrip_Checksum()
    {
        var (o, r) = GetPair();
        Assert.Equal(o.RomChecksum, r.RomChecksum);
    }

    [SkippableFact]
    public void RoundTrip_MapMode()
    {
        var (o, r) = GetPair();
        Assert.Equal(o.MapMode, r.MapMode);
    }

    [SkippableFact]
    public void RoundTrip_Speed()
    {
        var (o, r) = GetPair();
        Assert.Equal(o.Speed, r.Speed);
    }

    // ── RomBytes ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void RoundTrip_ByteCount()
    {
        var (o, r) = GetPair();
        Assert.Equal(o.Bytes.Length, r.Bytes.Length);
    }

    [SkippableFact]
    public void RoundTrip_ByteTypes()
    {
        var (o, r) = GetPair();
        for (var i = 0; i < o.Bytes.Length; i++)
            Assert.Equal(o.Bytes[i].Type, r.Bytes[i].Type);
    }

    [SkippableFact]
    public void RoundTrip_MFlags()
    {
        var (o, r) = GetPair();
        for (var i = 0; i < o.Bytes.Length; i++)
            Assert.Equal(o.Bytes[i].MFlag, r.Bytes[i].MFlag);
    }

    [SkippableFact]
    public void RoundTrip_XFlags()
    {
        var (o, r) = GetPair();
        for (var i = 0; i < o.Bytes.Length; i++)
            Assert.Equal(o.Bytes[i].XFlag, r.Bytes[i].XFlag);
    }

    [SkippableFact]
    public void RoundTrip_DataBank()
    {
        var (o, r) = GetPair();
        for (var i = 0; i < o.Bytes.Length; i++)
            Assert.Equal(o.Bytes[i].DataBank, r.Bytes[i].DataBank);
    }

    [SkippableFact]
    public void RoundTrip_DirectPage()
    {
        var (o, r) = GetPair();
        for (var i = 0; i < o.Bytes.Length; i++)
            Assert.Equal(o.Bytes[i].DirectPage, r.Bytes[i].DirectPage);
    }

    [SkippableFact]
    public void RoundTrip_Flow()
    {
        var (o, r) = GetPair();
        for (var i = 0; i < o.Bytes.Length; i++)
            Assert.Equal(o.Bytes[i].Flow, r.Bytes[i].Flow);
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public void RoundTrip_LabelCount()
    {
        var (o, r) = GetPair();
        Assert.Equal(o.Labels.Count, r.Labels.Count);
    }

    [SkippableFact]
    public void RoundTrip_LabelNames()
    {
        var (o, r) = GetPair();
        foreach (var (key, name) in o.Labels)
            Assert.Equal(name, r.Labels[key]);
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void RoundTrip_CommentCount()
    {
        var (o, r) = GetPair();
        Assert.Equal(o.Comments.Count, r.Comments.Count);
    }

    [SkippableFact]
    public void RoundTrip_Comments()
    {
        var (o, r) = GetPair();
        foreach (var (key, text) in o.Comments)
            Assert.Equal(text, r.Comments[key]);
    }

    // ── Sanity ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Export_IsValidXml()
    {
        if (!File.Exists(DizRawPath))
            throw new SkipException($"Test file not found: {DizRawPath}");

        var xml      = File.ReadAllText(DizRawPath);
        var original = DizProjectImporter.Import(xml);
        var exported = DizProjectExporter.Export(original);

        // Must parse without exception.
        _ = System.Xml.Linq.XDocument.Parse(exported);
    }
}
