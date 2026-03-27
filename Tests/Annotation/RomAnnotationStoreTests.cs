using Xunit;
using Mesen.Annotation;

namespace Mesen.Tests.Annotation;

public class RomAnnotationStoreTests
{
    private static RomAnnotationStore MakeStore(
        string gameName       = "TEST",
        uint checksum         = 0,
        RomMapMode mapMode    = RomMapMode.LoRom,
        RomSpeed speed        = RomSpeed.SlowRom,
        int saveVersion       = 104,
        ByteAnnotation[]? bytes = null,
        Dictionary<int, string>? labels        = null,
        Dictionary<int, string>? labelComments = null,
        Dictionary<int, string>? comments      = null) =>
        new()
        {
            RomGameName   = gameName,
            RomChecksum   = checksum,
            MapMode       = mapMode,
            Speed         = speed,
            SaveVersion   = saveVersion,
            Bytes         = bytes         ?? [],
            Labels        = labels        ?? new Dictionary<int, string>(),
            LabelComments = labelComments ?? new Dictionary<int, string>(),
            Comments      = comments      ?? new Dictionary<int, string>(),
        };

    [Fact]
    public void Properties_RoundTrip()
    {
        var store = MakeStore(
            gameName: "MY GAME",
            checksum: 0xDEADBEEF,
            mapMode: RomMapMode.HiRom,
            speed: RomSpeed.FastRom,
            saveVersion: 103);

        Assert.Equal("MY GAME",       store.RomGameName);
        Assert.Equal(0xDEADBEEFu,     store.RomChecksum);
        Assert.Equal(RomMapMode.HiRom, store.MapMode);
        Assert.Equal(RomSpeed.FastRom, store.Speed);
        Assert.Equal(103,              store.SaveVersion);
    }

    [Fact]
    public void Bytes_AccessibleByIndex()
    {
        var annotation = new ByteAnnotation { Type = ByteType.Opcode };
        var store = MakeStore(bytes: [annotation, ByteAnnotation.Default]);

        Assert.Equal(2,              store.Bytes.Length);
        Assert.Equal(ByteType.Opcode, store.Bytes[0].Type);
        Assert.Equal(ByteType.Unreached, store.Bytes[1].Type);
    }

    [Fact]
    public void Labels_LookupByAddress()
    {
        var store = MakeStore(labels: new() { [0x8000] = "Start" });
        Assert.Equal("Start", store.Labels[0x8000]);
    }

    [Fact]
    public void Comments_LookupByAddress()
    {
        var store = MakeStore(comments: new() { [0x8000] = "entry point" });
        Assert.Equal("entry point", store.Comments[0x8000]);
    }

    [Fact]
    public void EmptyStore_NullSafe()
    {
        var store = MakeStore();
        Assert.Equal("TEST", store.RomGameName);
        Assert.Empty(store.Bytes);
        Assert.Empty(store.Labels);
        Assert.Empty(store.LabelComments);
        Assert.Empty(store.Comments);
    }
}
