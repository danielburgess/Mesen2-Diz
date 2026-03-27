namespace Mesen.Annotation;

/// <summary>
/// Per-byte classification for a ROM address. Numeric values are identical to
/// DiztinGUIsh's FlagType enum and must never change — they are the wire format
/// for .diz/.dizraw import/export.
///
/// Encoding: high nibble = width category, low nibble = sub-type within category.
///   0x0_ = unreached
///   0x1_ = code (opcode / operand)
///   0x2_ = 8-bit data and variants
///   0x3_ = 16-bit data and pointers
///   0x4_ = 24-bit data and pointers
///   0x5_ = 32-bit data and pointers
///   0x6_ = text
/// </summary>
public enum ByteType : byte
{
    Unreached  = 0x00,
    Opcode     = 0x10,
    Operand    = 0x11,
    Data8      = 0x20,
    Graphics   = 0x21,
    Music      = 0x22,
    Empty      = 0x23,
    Data16     = 0x30,
    Pointer16  = 0x31,
    Data24     = 0x40,
    Pointer24  = 0x41,
    Data32     = 0x50,
    Pointer32  = 0x51,
    Text       = 0x60,
}

public static class ByteTypeExtensions
{
    /// <summary>True for Opcode and Operand only.</summary>
    public static bool IsCode(this ByteType t) =>
        t == ByteType.Opcode || t == ByteType.Operand;

    /// <summary>True for everything except Unreached, Opcode, and Operand.</summary>
    public static bool IsData(this ByteType t) =>
        t != ByteType.Unreached && !t.IsCode();
}
