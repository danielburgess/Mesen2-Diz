using Xunit;
using Mesen.Annotation;

namespace Mesen.Tests.Annotation;

public class SnesAddressConverterTests
{
    // ── LoRom → ROM offset ────────────────────────────────────────────────────

    [Fact]
    public void LoRom_Bank00_8000_MapsTo_Offset0()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x008000, RomMapMode.LoRom, out var off));
        Assert.Equal(0, off);
    }

    [Fact]
    public void LoRom_Bank00_FFFF_MapsTo_Offset7FFF()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x00FFFF, RomMapMode.LoRom, out var off));
        Assert.Equal(0x7FFF, off);
    }

    [Fact]
    public void LoRom_Bank01_8000_MapsTo_Offset8000()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x018000, RomMapMode.LoRom, out var off));
        Assert.Equal(0x8000, off);
    }

    [Fact]
    public void LoRom_Bank7D_FFFF_MapsTo_MaxOffset()
    {
        // 0x7D * 0x8000 + 0x7FFF = 0x3EFFFF
        Assert.True(SnesAddressConverter.TryToRomOffset(0x7DFFFF, RomMapMode.LoRom, out var off));
        Assert.Equal(0x3EFFFF, off);
    }

    [Fact]
    public void LoRom_Mirror_Bank80_SameAsBank00()
    {
        SnesAddressConverter.TryToRomOffset(0x008000, RomMapMode.LoRom, out var off00);
        SnesAddressConverter.TryToRomOffset(0x808000, RomMapMode.LoRom, out var off80);
        Assert.Equal(off00, off80);
    }

    [Fact]
    public void LoRom_Mirror_BankFD_SameAsBankFD_Mod80_Is7D()
    {
        // Bank 0xFD % 0x80 = 0x7D — both should give the same ROM offset.
        // Bank 0x7D is the last valid non-WRAM LoRom bank; 0xFD is its upper mirror.
        SnesAddressConverter.TryToRomOffset(0xFD8000, RomMapMode.LoRom, out var offFD);
        SnesAddressConverter.TryToRomOffset(0x7D8000, RomMapMode.LoRom, out var off7D);
        Assert.Equal(off7D, offFD);
    }

    // ── LoRom → false cases ───────────────────────────────────────────────────

    [Fact]
    public void LoRom_PageBelow8000_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x007FFF, RomMapMode.LoRom, out _));
    }

    [Fact]
    public void LoRom_Page0000_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x000000, RomMapMode.LoRom, out _));
    }

    [Fact]
    public void LoRom_WramBank7E_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x7E8000, RomMapMode.LoRom, out _));
    }

    [Fact]
    public void LoRom_WramBank7F_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x7F0000, RomMapMode.LoRom, out _));
    }

    // ── LoRom round-trip ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x000000)]
    [InlineData(0x008000)]
    [InlineData(0x00FFFF)]
    [InlineData(0x018000)]
    [InlineData(0x3EFFFF)]
    public void LoRom_RoundTrip_Offset(int romOffset)
    {
        var romSize = 0x400000; // 4 MB
        Assert.True(SnesAddressConverter.TryToSnesAddress(romOffset, romSize, RomMapMode.LoRom, out var snes));
        Assert.True(SnesAddressConverter.TryToRomOffset(snes, RomMapMode.LoRom, out var back));
        Assert.Equal(romOffset, back);
    }

    [Theory]
    [InlineData(0x008000)]
    [InlineData(0x00FFFF)]
    [InlineData(0x018000)]
    [InlineData(0x7DFFFF)]
    public void LoRom_RoundTrip_SnesAddress(int snesAddress)
    {
        var romSize = 0x400000;
        Assert.True(SnesAddressConverter.TryToRomOffset(snesAddress, RomMapMode.LoRom, out var offset));
        Assert.True(SnesAddressConverter.TryToSnesAddress(offset, romSize, RomMapMode.LoRom, out var back));
        // Canonical address may differ (e.g. mirrors); but re-converting back gives same offset.
        Assert.True(SnesAddressConverter.TryToRomOffset(back, RomMapMode.LoRom, out var recheck));
        Assert.Equal(offset, recheck);
    }

    // ── LoRom TryToSnesAddress bounds ─────────────────────────────────────────

    [Fact]
    public void LoRom_ToSnes_NegativeOffset_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToSnesAddress(-1, 0x80000, RomMapMode.LoRom, out _));
    }

    [Fact]
    public void LoRom_ToSnes_OffsetBeyondRomSize_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToSnesAddress(0x80000, 0x80000, RomMapMode.LoRom, out _));
    }

    // ── HiRom → ROM offset ────────────────────────────────────────────────────

    [Fact]
    public void HiRom_Bank40_0000_MapsTo_Offset0()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x400000, RomMapMode.HiRom, out var off));
        Assert.Equal(0, off);
    }

    [Fact]
    public void HiRom_Bank40_FFFF_MapsTo_OffsetFFFF()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x40FFFF, RomMapMode.HiRom, out var off));
        Assert.Equal(0xFFFF, off);
    }

    [Fact]
    public void HiRom_Bank41_0000_MapsTo_Offset10000()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x410000, RomMapMode.HiRom, out var off));
        Assert.Equal(0x10000, off);
    }

    [Fact]
    public void HiRom_Bank00_8000_MapsTo_Offset8000()
    {
        // Mirror: bank 00 maps same as bank 40 for pages 8000+
        Assert.True(SnesAddressConverter.TryToRomOffset(0x008000, RomMapMode.HiRom, out var off));
        Assert.Equal(0x8000, off);
    }

    [Fact]
    public void HiRom_BankC0_SameAsBank40()
    {
        SnesAddressConverter.TryToRomOffset(0x400000, RomMapMode.HiRom, out var off40);
        SnesAddressConverter.TryToRomOffset(0xC00000, RomMapMode.HiRom, out var offC0);
        Assert.Equal(off40, offC0);
    }

    [Fact]
    public void HiRom_Bank80_8000_SameAsBank00_8000()
    {
        SnesAddressConverter.TryToRomOffset(0x008000, RomMapMode.HiRom, out var off00);
        SnesAddressConverter.TryToRomOffset(0x808000, RomMapMode.HiRom, out var off80);
        Assert.Equal(off00, off80);
    }

    // ── HiRom → false cases ───────────────────────────────────────────────────

    [Fact]
    public void HiRom_Bank00_PageBelow8000_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x007FFF, RomMapMode.HiRom, out _));
    }

    [Fact]
    public void HiRom_Bank3F_PageBelow8000_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x3F0000, RomMapMode.HiRom, out _));
    }

    // ── HiRom round-trip ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x000000)]
    [InlineData(0x008000)]
    [InlineData(0x00FFFF)]
    [InlineData(0x10000)]
    [InlineData(0x3FFFFF)]
    public void HiRom_RoundTrip_Offset(int romOffset)
    {
        var romSize = 0x400000;
        Assert.True(SnesAddressConverter.TryToSnesAddress(romOffset, romSize, RomMapMode.HiRom, out var snes));
        Assert.True(SnesAddressConverter.TryToRomOffset(snes, RomMapMode.HiRom, out var back));
        Assert.Equal(romOffset, back);
    }

    // ── Unsupported modes ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(RomMapMode.SuperFx)]
    [InlineData(RomMapMode.SuperMmc)]
    [InlineData(RomMapMode.ExSa1Rom)]
    public void UnsupportedMode_ToOffset_Throws(RomMapMode mode)
    {
        Assert.Throws<NotSupportedException>(() =>
            SnesAddressConverter.TryToRomOffset(0x008000, mode, out _));
    }

    [Theory]
    [InlineData(RomMapMode.SuperFx)]
    [InlineData(RomMapMode.SuperMmc)]
    [InlineData(RomMapMode.ExSa1Rom)]
    public void UnsupportedMode_ToSnes_Throws(RomMapMode mode)
    {
        Assert.Throws<NotSupportedException>(() =>
            SnesAddressConverter.TryToSnesAddress(0, 0x100000, mode, out _));
    }

    // ── ExHiRom → ROM offset ──────────────────────────────────────────────────

    [Fact]
    public void ExHiRom_BankC0_0000_MapsTo_Offset0()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0xC00000, RomMapMode.ExHiRom, out var off));
        Assert.Equal(0, off);
    }

    [Fact]
    public void ExHiRom_BankFF_FFFF_MapsTo_Offset3FFFFF()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0xFFFFFF, RomMapMode.ExHiRom, out var off));
        Assert.Equal(0x3FFFFF, off);
    }

    [Fact]
    public void ExHiRom_Bank40_0000_MapsTo_Offset400000()
    {
        // Second half: banks 40–7D start at 0x400000.
        Assert.True(SnesAddressConverter.TryToRomOffset(0x400000, RomMapMode.ExHiRom, out var off));
        Assert.Equal(0x400000, off);
    }

    [Fact]
    public void ExHiRom_Bank7D_FFFF_MapsTo_Offset7DFFFF()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x7DFFFF, RomMapMode.ExHiRom, out var off));
        Assert.Equal(0x7DFFFF, off);
    }

    [Fact]
    public void ExHiRom_Mirror80_SameAsC0()
    {
        // Bank 80 page 8000 mirrors bank C0 page 8000.
        SnesAddressConverter.TryToRomOffset(0xC08000, RomMapMode.ExHiRom, out var offC0);
        SnesAddressConverter.TryToRomOffset(0x808000, RomMapMode.ExHiRom, out var off80);
        Assert.Equal(offC0, off80);
    }

    [Fact]
    public void ExHiRom_Mirror00_SameAs40()
    {
        // Bank 00 page 8000 mirrors bank 40 page 8000.
        SnesAddressConverter.TryToRomOffset(0x408000, RomMapMode.ExHiRom, out var off40);
        SnesAddressConverter.TryToRomOffset(0x008000, RomMapMode.ExHiRom, out var off00);
        Assert.Equal(off40, off00);
    }

    [Fact]
    public void ExHiRom_Bank80_PageBelow8000_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x807FFF, RomMapMode.ExHiRom, out _));
    }

    [Fact]
    public void ExHiRom_Bank00_PageBelow8000_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x007FFF, RomMapMode.ExHiRom, out _));
    }

    [Fact]
    public void ExHiRom_WramBank7E_ReturnsFalse()
    {
        Assert.False(SnesAddressConverter.TryToRomOffset(0x7E0000, RomMapMode.ExHiRom, out _));
    }

    [Theory]
    [InlineData(0x000000)]
    [InlineData(0x3FFFFF)]
    [InlineData(0x400000)]
    [InlineData(0x7DFFFF)]
    public void ExHiRom_RoundTrip_Offset(int romOffset)
    {
        int romSize = 0x800000; // 8 MB
        Assert.True(SnesAddressConverter.TryToSnesAddress(romOffset, romSize, RomMapMode.ExHiRom, out var snes));
        Assert.True(SnesAddressConverter.TryToRomOffset(snes, RomMapMode.ExHiRom, out var back));
        Assert.Equal(romOffset, back);
    }

    // ── ExLoRom — uses LoRom formula ──────────────────────────────────────────

    [Fact]
    public void ExLoRom_Bank00_8000_MapsTo_Offset0()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x008000, RomMapMode.ExLoRom, out var off));
        Assert.Equal(0, off);
    }

    [Fact]
    public void ExLoRom_RoundTrip()
    {
        int romSize = 0x400000;
        Assert.True(SnesAddressConverter.TryToSnesAddress(0x18000, romSize, RomMapMode.ExLoRom, out var snes));
        Assert.True(SnesAddressConverter.TryToRomOffset(snes, RomMapMode.ExLoRom, out var back));
        Assert.Equal(0x18000, back);
    }

    // ── Sa1Rom — uses LoRom formula ───────────────────────────────────────────

    [Fact]
    public void Sa1Rom_Bank00_8000_MapsTo_Offset0()
    {
        Assert.True(SnesAddressConverter.TryToRomOffset(0x008000, RomMapMode.Sa1Rom, out var off));
        Assert.Equal(0, off);
    }

    [Fact]
    public void Sa1Rom_RoundTrip()
    {
        int romSize = 0x400000;
        Assert.True(SnesAddressConverter.TryToSnesAddress(0x8000, romSize, RomMapMode.Sa1Rom, out var snes));
        Assert.True(SnesAddressConverter.TryToRomOffset(snes, RomMapMode.Sa1Rom, out var back));
        Assert.Equal(0x8000, back);
    }
}
