using Xunit;
using Mesen.Annotation;
using Mesen.Annotation.Diz;

namespace Mesen.Tests.Annotation.Diz;

/// <summary>
/// Integration tests that parse the real zamn.dizraw file.
/// The file must exist at the hard-coded path; tests are skipped gracefully
/// when it is absent so that the CI build (which has no access to the file)
/// stays green.
/// </summary>
public class DizProjectImporterIntegrationTests
{
    private const string DizRawPath =
        "/mnt/data/Projects/zamn_disassembly/zamn.dizraw";

    private static readonly Lazy<(bool exists, RomAnnotationStore? store, Exception? error)> _result =
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

    private static RomAnnotationStore GetStore()
    {
        var (exists, store, error) = _result.Value;
        if (!exists)
            throw new SkipException($"Test file not found: {DizRawPath}");
        if (error is not null)
            throw new InvalidOperationException("Import threw unexpectedly.", error);
        return store!;
    }

    // ── Header fields ─────────────────────────────────────────────────────────

    [SkippableFact]
    public void Import_GameName()
    {
        Assert.Equal("ZOMBIES ATE MY NEIGHB", GetStore().RomGameName);
    }

    [SkippableFact]
    public void Import_Checksum()
    {
        Assert.Equal(4111207155u, GetStore().RomChecksum);
    }

    [SkippableFact]
    public void Import_MapMode()
    {
        Assert.Equal(RomMapMode.LoRom, GetStore().MapMode);
    }

    [SkippableFact]
    public void Import_Speed()
    {
        Assert.Equal(RomSpeed.SlowRom, GetStore().Speed);
    }

    [SkippableFact]
    public void Import_SaveVersion()
    {
        Assert.Equal(104, GetStore().SaveVersion);
    }

    // ── RomBytes ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Import_Bytes_NonEmpty()
    {
        Assert.NotEmpty(GetStore().Bytes);
    }

    [SkippableFact]
    public void Import_FirstByte_IsOpcode()
    {
        // First byte of ZAMN ROM is the reset vector opcode.
        Assert.Equal(ByteType.Opcode, GetStore().Bytes[0].Type);
    }

    [SkippableFact]
    public void Import_SecondByte_IsOperand()
    {
        Assert.Equal(ByteType.Operand, GetStore().Bytes[1].Type);
    }

    [SkippableFact]
    public void Import_Bytes_ContainsMixedTypes()
    {
        var bytes = GetStore().Bytes;
        Assert.Contains(bytes, b => b.Type == ByteType.Opcode);
        Assert.Contains(bytes, b => b.Type == ByteType.Operand);
        Assert.Contains(bytes, b => b.Type == ByteType.Unreached);
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Import_Labels_Count()
    {
        Assert.Equal(22, GetStore().Labels.Count);
    }

    [SkippableFact]
    public void Import_Labels_AddressRange()
    {
        foreach (var key in GetStore().Labels.Keys)
        {
            Assert.InRange(key, 32768, 65534);
        }
    }

    [SkippableFact]
    public void Import_LabelComments_EmptyForZamn()
    {
        // All 22 labels in zamn.dizraw have empty Comment attributes.
        Assert.Empty(GetStore().LabelComments);
    }

    [SkippableFact]
    public void Import_Labels_ContainsEmptyNameLabel()
    {
        // Address 33496 has a label with an empty Name in zamn.dizraw.
        var store = GetStore();
        Assert.True(store.Labels.ContainsKey(33496));
        Assert.Equal("", store.Labels[33496]);
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Import_Comments_NonEmpty()
    {
        Assert.NotEmpty(GetStore().Comments);
    }

    [SkippableFact]
    public void Import_Comments_KeysAreSnesAddresses()
    {
        // All comment keys must be plausible SNES addresses (0x0000 – 0xFFFFFF).
        foreach (var key in GetStore().Comments.Keys)
        {
            Assert.InRange(key, 0, 0xFF_FFFF);
        }
    }

    [SkippableFact]
    public void Import_Comments_ValuesNonEmpty()
    {
        foreach (var value in GetStore().Comments.Values)
        {
            Assert.False(string.IsNullOrEmpty(value),
                "Comments dictionary must not store empty-string values.");
        }
    }

    // ── No exceptions ─────────────────────────────────────────────────────────

    [SkippableFact]
    public void Import_DoesNotThrow()
    {
        // If GetStore() returns without throwing, the import succeeded.
        _ = GetStore();
    }
}
