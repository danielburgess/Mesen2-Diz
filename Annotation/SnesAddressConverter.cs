namespace Mesen.Annotation;

/// <summary>
/// Pure SNES address ↔ SnesPrgRom offset translation for LoRom, HiRom,
/// ExHiRom, ExLoRom, and Sa1Rom.
///
/// Formulas are derived directly from Mesen's BaseCartridge.cpp memory-map
/// registration and RamHandler::GetAbsoluteAddress, so the results are
/// byte-identical to what Mesen's live debugger would return.
///
/// Address layout reminder:
///   snesAddress = (bank &lt;&lt; 16) | (page &amp; 0xFFFF)
///   bank = snesAddress >> 16  (0x00–0xFF)
///   page = snesAddress &amp; 0xFFFF
/// </summary>
public static class SnesAddressConverter
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Convert a 24-bit SNES address to a SnesPrgRom ROM offset.
    /// Returns <c>false</c> for addresses that do not fall in the ROM window
    /// (WRAM, hardware registers, SaveRam, unmapped).
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown for map modes that are not yet implemented
    /// (SuperFx, SuperMmc, ExSa1Rom).
    /// </exception>
    public static bool TryToRomOffset(int snesAddress, RomMapMode mapMode, out int romOffset)
    {
        romOffset = 0;
        int bank = (snesAddress >> 16) & 0xFF;
        int page = snesAddress & 0xFFFF;

        return mapMode switch
        {
            RomMapMode.LoRom   => TryLoRomToOffset(bank, page, out romOffset),
            RomMapMode.HiRom   => TryHiRomToOffset(bank, page, out romOffset),
            RomMapMode.ExHiRom => TryExHiRomToOffset(bank, page, out romOffset),
            // ExLoRom uses the LoRom formula — same RegisterHandler calls in Mesen.
            RomMapMode.ExLoRom => TryLoRomToOffset(bank, page, out romOffset),
            // SA-1 games are LoRom from the SNES CPU's perspective.
            RomMapMode.Sa1Rom  => TryLoRomToOffset(bank, page, out romOffset),
            _ => throw new NotSupportedException(
                     $"RomMapMode.{mapMode} address translation is not yet implemented.")
        };
    }

    /// <summary>
    /// Convert a SnesPrgRom ROM offset back to the canonical SNES address.
    ///
    /// Canonical addresses:
    ///   LoRom/ExLoRom/Sa1Rom — lowest bank in range 00–7D, page 8000+
    ///   HiRom                — lowest bank in range 40–7D, page 0000+
    ///   ExHiRom              — banks C0–FF for offset &lt; 0x400000,
    ///                          banks 40–7D for offset ≥ 0x400000
    ///
    /// Returns <c>false</c> when <paramref name="romOffset"/> is out of range
    /// for the given <paramref name="romSizeBytes"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown for map modes that are not yet implemented
    /// (SuperFx, SuperMmc, ExSa1Rom).
    /// </exception>
    public static bool TryToSnesAddress(
        int romOffset, int romSizeBytes, RomMapMode mapMode, out int snesAddress)
    {
        snesAddress = 0;
        if (romOffset < 0 || romOffset >= romSizeBytes)
            return false;

        return mapMode switch
        {
            RomMapMode.LoRom   => TryLoRomToSnes(romOffset, out snesAddress),
            RomMapMode.HiRom   => TryHiRomToSnes(romOffset, out snesAddress),
            RomMapMode.ExHiRom => TryExHiRomToSnes(romOffset, out snesAddress),
            RomMapMode.ExLoRom => TryLoRomToSnes(romOffset, out snesAddress),
            RomMapMode.Sa1Rom  => TryLoRomToSnes(romOffset, out snesAddress),
            _ => throw new NotSupportedException(
                     $"RomMapMode.{mapMode} address translation is not yet implemented.")
        };
    }

    // ── LoRom ─────────────────────────────────────────────────────────────────
    //
    // BaseCartridge.cpp:
    //   mm.RegisterHandler(0x00, 0x7D, 0x8000, 0xFFFF, _prgRomHandlers);
    //   mm.RegisterHandler(0x80, 0xFF, 0x8000, 0xFFFF, _prgRomHandlers);
    //
    // RamHandler: offset = _offset + (addr & 0xFFF)
    // Handler[n] covers ROM bytes [n*0x1000, n*0x1000+0xFFF].
    // Each bank 8-page window (8000–FFFF) maps to 8 consecutive handlers.
    //
    // Therefore: romOffset = (bank % 0x80) * 0x8000 + (page & 0x7FFF)
    //
    // Also used for ExLoRom (same handlers) and Sa1Rom (LoRom from SNES CPU view).

    private static bool TryLoRomToOffset(int bank, int page, out int romOffset)
    {
        romOffset = 0;

        // WRAM banks are never ROM.
        if (bank == 0x7E || bank == 0x7F)
            return false;

        // ROM is only accessible at pages 8000–FFFF.
        if (page < 0x8000)
            return false;

        romOffset = (bank % 0x80) * 0x8000 + (page & 0x7FFF);
        return true;
    }

    private static bool TryLoRomToSnes(int romOffset, out int snesAddress)
    {
        // bank = romOffset / 0x8000  (stays in 00–7D for a ≤ 4MB ROM)
        // page = 0x8000 + romOffset % 0x8000
        int bank = romOffset / 0x8000;
        int page = 0x8000 + (romOffset % 0x8000);
        snesAddress = (bank << 16) | page;
        return true;
    }

    // ── HiRom ─────────────────────────────────────────────────────────────────
    //
    // BaseCartridge.cpp:
    //   mm.RegisterHandler(0x00, 0x3F, 0x8000, 0xFFFF, _prgRomHandlers, 8);
    //   mm.RegisterHandler(0x40, 0x7D, 0x0000, 0xFFFF, _prgRomHandlers, 0);
    //   mm.RegisterHandler(0x80, 0xBF, 0x8000, 0xFFFF, _prgRomHandlers, 8);
    //   mm.RegisterHandler(0xC0, 0xFF, 0x0000, 0xFFFF, _prgRomHandlers, 0);
    //
    // Both regions produce the same formula:
    //   romOffset = (bank & 0x3F) * 0x10000 + (page & 0xFFFF)
    //
    // Validity:
    //   bank 00–3F / 80–BF: only pages 8000–FFFF are ROM
    //   bank 40–7D / C0–FF: all pages are ROM

    private static bool TryHiRomToOffset(int bank, int page, out int romOffset)
    {
        romOffset = 0;

        int bankLo = bank & 0x3F;   // strip mirror bit

        bool isFullBank = bank is (>= 0x40 and <= 0x7D) or (>= 0xC0 and <= 0xFF);
        bool isHalfBank = bank is (>= 0x00 and <= 0x3F) or (>= 0x80 and <= 0xBF);

        if (isHalfBank && page < 0x8000)
            return false;

        if (!isFullBank && !isHalfBank)
            return false;

        romOffset = bankLo * 0x10000 + (page & 0xFFFF);
        return true;
    }

    private static bool TryHiRomToSnes(int romOffset, out int snesAddress)
    {
        // Canonical: prefer banks 40–7D (full-bank, no page restriction).
        // Banks 7E–7F are always WRAM, so offsets that would land there
        // overflow into the C0–FF mirror instead.
        int bank = 0x40 + romOffset / 0x10000;
        if (bank > 0x7D)
            bank = 0xC0 + (romOffset / 0x10000) % 0x40;
        int page = romOffset % 0x10000;
        snesAddress = (bank << 16) | page;
        return true;
    }

    // ── ExHiRom ───────────────────────────────────────────────────────────────
    //
    // BaseCartridge.cpp:
    //   // First half (offsets 0x000000–0x3FFFFF):
    //   mm.RegisterHandler(0xC0, 0xFF, 0x0000, 0xFFFF, _prgRomHandlers, 0);      // primary
    //   mm.RegisterHandler(0x80, 0xBF, 0x8000, 0xFFFF, _prgRomHandlers, 8);      // mirror
    //   // Second half (offsets 0x400000+):
    //   mm.RegisterHandler(0x40, 0x7D, 0x0000, 0xFFFF, _prgRomHandlers, 0, 0x400);  // primary
    //   mm.RegisterHandler(0x00, 0x3F, 0x8000, 0xFFFF, _prgRomHandlers, 8, 0x400);  // mirror
    //
    // Derived formulas:
    //   banks C0–FF and mirrors 80–BF: romOffset = (bank & 0x3F) * 0x10000 + page
    //   banks 40–7D and mirrors 00–3F: romOffset = 0x400000 + (bank & 0x3F) * 0x10000 + page
    //
    // Validity:
    //   banks 80–BF and 00–3F: only pages 8000–FFFF are ROM (upper half only)
    //   banks C0–FF and 40–7D: all pages are ROM
    //   banks 7E–7F: always WRAM

    private static bool TryExHiRomToOffset(int bank, int page, out int romOffset)
    {
        romOffset = 0;
        if (bank == 0x7E || bank == 0x7F) return false;

        if (bank >= 0x80)
        {
            // Banks 80–BF: upper half only; banks C0–FF: all pages.
            if (bank <= 0xBF && page < 0x8000) return false;
            romOffset = (bank & 0x3F) * 0x10000 + page;
            return true;
        }

        // Banks 00–3F: upper half only; banks 40–7D: all pages.
        if (bank <= 0x3F && page < 0x8000) return false;
        romOffset = 0x400000 + (bank & 0x3F) * 0x10000 + page;
        return true;
    }

    private static bool TryExHiRomToSnes(int romOffset, out int snesAddress)
    {
        int bank, page;
        if (romOffset < 0x400000)
        {
            // Canonical: banks C0–FF (full-bank primary region).
            bank = 0xC0 + romOffset / 0x10000;
            page = romOffset % 0x10000;
        }
        else
        {
            // Canonical: banks 40–7D (full-bank primary region for second half).
            bank = 0x40 + (romOffset - 0x400000) / 0x10000;
            page = (romOffset - 0x400000) % 0x10000;
        }
        snesAddress = (bank << 16) | page;
        return true;
    }
}
