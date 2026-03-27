namespace Mesen.Annotation;

/// <summary>
/// Immutable snapshot of all annotation data read from a DiztinGUIsh project file.
///
/// Address keys in Labels, LabelComments, and Comments are raw SNES addresses
/// as stored in the .dizraw file — not ROM offsets. Address translation is the
/// caller's responsibility and is out of scope here.
///
/// LabelComments only contains entries for labels that have a non-empty comment.
/// </summary>
public sealed class RomAnnotationStore
{
    /// <summary>ROM cartridge title from the SNES header (trimmed).</summary>
    public required string RomGameName { get; init; }

    /// <summary>ROM CRC32/checksum as stored in the project file.</summary>
    public required uint RomChecksum { get; init; }

    /// <summary>SNES memory mapping hardware type.</summary>
    public required RomMapMode MapMode { get; init; }

    /// <summary>ROM speed (SlowROM / FastROM).</summary>
    public required RomSpeed Speed { get; init; }

    /// <summary>DiztinGUIsh project file format version that was read.</summary>
    public required int SaveVersion { get; init; }

    /// <summary>
    /// Per-byte annotations, one entry per ROM byte, in ROM offset order.
    /// </summary>
    public required ByteAnnotation[] Bytes { get; init; }

    /// <summary>SNES address → label name. Empty-name labels are included.</summary>
    public required IReadOnlyDictionary<int, string> Labels { get; init; }

    /// <summary>
    /// SNES address → comment attached to the label at that address.
    /// Only present when the comment is non-empty.
    /// </summary>
    public required IReadOnlyDictionary<int, string> LabelComments { get; init; }

    /// <summary>SNES address → inline disassembly comment.</summary>
    public required IReadOnlyDictionary<int, string> Comments { get; init; }
}
