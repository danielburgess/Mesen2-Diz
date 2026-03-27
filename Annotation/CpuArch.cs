namespace Mesen.Annotation;

/// <summary>
/// Which CPU is responsible for executing a given byte. Numeric values are
/// identical to DiztinGUIsh's Architecture enum and must never change — they
/// are the wire format for .diz/.dizraw import/export.
///
/// Note: only Cpu65C816 is supported until SNES feature parity with
/// DiztinGUIsh is reached.
/// </summary>
public enum CpuArch : byte
{
    Cpu65C816 = 0x00,
    Spc700    = 0x01,
    SuperFx   = 0x02,
}
