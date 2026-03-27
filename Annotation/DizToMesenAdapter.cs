using System.Text;

namespace Mesen.Annotation;

/// <summary>
/// Converts a <see cref="RomAnnotationStore"/> to the two on-disk file
/// formats that Mesen's existing import machinery accepts:
///
/// <list type="bullet">
///   <item><see cref="ToCdlFileBytes"/> — binary .cdl file content
///         (magic + CRC32 header + one CDL byte per ROM byte)</item>
///   <item><see cref="ToMlbText"/> — UTF-8 .mlb label file content
///         (one <c>MemoryType:Address:Label:Comment</c> line per entry)</item>
/// </list>
///
/// Neither method touches the running Mesen process; they produce byte/string
/// content ready to write to disk or hand to Mesen's import API.
/// </summary>
public static class DizToMesenAdapter
{
    // ── CDL ───────────────────────────────────────────────────────────────────

    // Mesen CdlFlags values (mirrors DebugTypes.h CdlFlags namespace).
    private const byte CdlNone         = 0x00;
    private const byte CdlCode         = 0x01;
    private const byte CdlData         = 0x02;
    private const byte CdlSubEntry     = 0x08;

    private static readonly byte[] CdlMagic = "CDLv2"u8.ToArray();
    private const int CdlHeaderSize = 9; // 5 magic + 4 CRC32

    /// <summary>
    /// Produce the raw bytes of a Mesen .cdl file.
    ///
    /// File layout (identical to CodeDataLogger::SaveCdlFile):
    /// <code>
    ///   "CDLv2"                — 5 bytes
    ///   store.RomChecksum LE   — 4 bytes
    ///   cdlByte[0..n-1]        — 1 byte per ROM byte
    /// </code>
    /// </summary>
    public static byte[] ToCdlFileBytes(RomAnnotationStore store)
    {
        var bytes  = store.Bytes;
        var result = new byte[CdlHeaderSize + bytes.Length];

        // Magic
        CdlMagic.CopyTo(result, 0);

        // CRC32, little-endian
        uint crc = store.RomChecksum;
        result[5] = (byte)(crc & 0xFF);
        result[6] = (byte)(crc >> 8  & 0xFF);
        result[7] = (byte)(crc >> 16 & 0xFF);
        result[8] = (byte)(crc >> 24 & 0xFF);

        // Per-byte CDL flags
        for (var i = 0; i < bytes.Length; i++)
            result[CdlHeaderSize + i] = ToCdlByte(bytes[i]);

        return result;
    }

    private static byte ToCdlByte(ByteAnnotation a)
    {
        return a.Type switch
        {
            ByteType.Opcode  => (a.Flow & InOutPoint.InPoint) != 0
                                    ? (byte)(CdlCode | CdlSubEntry)
                                    : CdlCode,
            ByteType.Operand => CdlCode,

            ByteType.Data8      or ByteType.Data16    or
            ByteType.Data24     or ByteType.Data32    or
            ByteType.Pointer16  or ByteType.Pointer24 or ByteType.Pointer32 or
            ByteType.Graphics   or ByteType.Music     or
            ByteType.Empty      or ByteType.Text      => CdlData,

            _ => CdlNone,   // Unreached
        };
    }

    // ── MLB ───────────────────────────────────────────────────────────────────

    // Mesen memory type string for SNES program ROM (matches MemoryType enum name).
    private const string SnesPrgRom = "SnesPrgRom";

    /// <summary>
    /// Produce the UTF-8 text content of a Mesen .mlb label file.
    ///
    /// Line format (matches <c>CodeLabel.ToString()</c>):
    /// <code>SnesPrgRom:HHHH:LabelName:Comment</code>
    ///
    /// Rules:
    /// <list type="bullet">
    ///   <item>Labels and comments whose SNES address does not translate to ROM
    ///         (e.g. WRAM, registers) are silently skipped.</item>
    ///   <item>Newlines inside comment text are escaped as <c>\n</c>.</item>
    ///   <item>Output is sorted by ROM offset ascending; for the same offset
    ///         a label-bearing line precedes a comment-only line.</item>
    /// </list>
    /// </summary>
    public static string ToMlbText(RomAnnotationStore store)
    {
        var mapMode = store.MapMode;

        // Collect: romOffset → (labelName, comment)
        // A single address can have both a label and a standalone comment;
        // we merge them into one CodeLabel entry (label wins, comment merges).
        var entries = new SortedDictionary<int, (string label, string comment)>();

        // Labels (may also have a label comment from LabelComments).
        foreach (var (snesAddr, name) in store.Labels)
        {
            if (!SnesAddressConverter.TryToRomOffset(snesAddr, mapMode, out var offset))
                continue;

            var labelComment = store.LabelComments.TryGetValue(snesAddr, out var lc) ? lc : "";
            entries[offset] = (name, labelComment);
        }

        // Inline comments: addresses not already covered by a label get their
        // own entry; addresses that already have a label merge the comment in.
        foreach (var (snesAddr, comment) in store.Comments)
        {
            if (!SnesAddressConverter.TryToRomOffset(snesAddr, mapMode, out var offset))
                continue;

            if (entries.TryGetValue(offset, out var existing))
            {
                // Prefer the label comment; fall back to inline comment if no label comment.
                var mergedComment = string.IsNullOrEmpty(existing.comment)
                    ? comment
                    : existing.comment;
                entries[offset] = (existing.label, mergedComment);
            }
            else
            {
                entries[offset] = ("", comment);
            }
        }

        // Render
        var sb = new StringBuilder(entries.Count * 48);
        foreach (var (offset, (label, comment)) in entries)
        {
            // Skip entries that have neither a label name nor a comment.
            if (string.IsNullOrEmpty(label) && string.IsNullOrEmpty(comment))
                continue;

            sb.Append(SnesPrgRom);
            sb.Append(':');
            sb.Append(offset.ToString("X4"));
            sb.Append(':');
            sb.Append(label);

            if (!string.IsNullOrEmpty(comment))
            {
                sb.Append(':');
                sb.Append(EscapeComment(comment));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string EscapeComment(string comment) =>
        comment.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
}
