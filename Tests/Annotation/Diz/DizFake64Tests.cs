using Xunit;
using Mesen.Annotation.Diz;

namespace Mesen.Tests.Annotation.Diz;

public class DizFake64Tests
{
    // ── Pinned encode values ───────────────────────────────────────────────────

    [Theory]
    [InlineData(  0, '0')]
    [InlineData(  4, 'B')]
    [InlineData( 28, 'H')]   // XFlag|MFlag|InPoint packed value used in sample data
    [InlineData(208, 'A')]
    [InlineData(252, '/')]
    public void Encode_KnownValues(byte input, char expected)
    {
        Assert.Equal(expected, DizFake64.Encode(input));
    }

    // ── Pinned decode values ───────────────────────────────────────────────────

    [Theory]
    [InlineData('0',   0)]
    [InlineData('B',   4)]
    [InlineData('H',  28)]
    [InlineData('A', 208)]
    [InlineData('/', 252)]
    public void Decode_KnownValues(char input, byte expected)
    {
        Assert.Equal(expected, DizFake64.Decode(input));
    }

    // ── Full round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_EncodeDecodeForEveryTableEntry()
    {
        foreach (var kvp in DizFake64.AllEntries)
        {
            var encoded = DizFake64.Encode(kvp.Key);
            var decoded = DizFake64.Decode(encoded);
            Assert.Equal(kvp.Key, decoded);
        }
    }

    // ── Invalid input ─────────────────────────────────────────────────────────

    [Fact]
    public void Decode_UnknownChar_Throws()
    {
        Assert.Throws<ArgumentException>(() => DizFake64.Decode('!'));
    }

    [Fact]
    public void Encode_InvalidValue_Throws()
    {
        // 1 is not a multiple of 4 and is not in the table
        Assert.Throws<ArgumentException>(() => DizFake64.Encode(1));
    }
}
