namespace Mesen.Annotation;

/// <summary>
/// SNES ROM mapping hardware type. Numeric values are identical to
/// DiztinGUIsh's RomMapMode enum and must never change (but can be added to) — they are the wire
/// format for .diz/.dizraw import/export.
/// </summary>
public enum RomMapMode : byte
{
    LoRom    = 0,
    HiRom    = 1,
    ExHiRom  = 2,
    Sa1Rom   = 3,
    ExSa1Rom = 4,
    SuperFx  = 5,
    SuperMmc = 6,
    ExLoRom  = 7,
}

/// <summary>
/// SNES ROM speed (SlowROM vs FastROM). Numeric values are identical to
/// DiztinGUIsh's RomSpeed enum and must never change — they are the wire
/// format for .diz/.dizraw import/export.
/// </summary>
public enum RomSpeed : byte
{
    SlowRom = 0,
    FastRom = 1,
    Unknown = 2,
}
