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

    // Mesen CdlFlags values (mirrors DebugApi.cs CdlFlags enum and DebugTypes.h).
    private const byte CdlNone         = 0x00;
    private const byte CdlCode         = 0x01;
    private const byte CdlData         = 0x02;
    private const byte CdlSubEntry     = 0x08;
    private const byte CdlIndexMode8   = 0x10;  // XFlag — index registers are 8-bit
    private const byte CdlMemoryMode8  = 0x20;  // MFlag — accumulator is 8-bit

    private static readonly byte[] CdlMagic = "CDLv2"u8.ToArray();
    private const int CdlHeaderSize = 9; // 5 magic + 4 CRC32

    /// <summary>
    /// Produce just the per-byte CDL flag array (no file header).
    ///
    /// This is the format expected by <c>DebugApi.SetCdlData</c>, which
    /// operates on raw flag bytes rather than a full .cdl file on disk.
    /// </summary>
    public static byte[] ToCdlDataBytes(RomAnnotationStore store)
    {
        var bytes  = store.Bytes;
        var result = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            result[i] = ToCdlByte(bytes[i]);
        return result;
    }

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
        var data   = ToCdlDataBytes(store);
        var result = new byte[CdlHeaderSize + data.Length];

        // Magic
        CdlMagic.CopyTo(result, 0);

        // CRC32, little-endian
        uint crc = store.RomChecksum;
        result[5] = (byte)(crc & 0xFF);
        result[6] = (byte)(crc >> 8  & 0xFF);
        result[7] = (byte)(crc >> 16 & 0xFF);
        result[8] = (byte)(crc >> 24 & 0xFF);

        // Per-byte CDL flags
        data.CopyTo(result, CdlHeaderSize);

        return result;
    }

    private static byte ToCdlByte(ByteAnnotation a)
    {
        byte flags = a.Type switch
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

        // M/X flags are only meaningful for code bytes (Mesen reads them on
        // code bytes to know the CPU mode at each instruction).
        if ((flags & CdlCode) != 0)
        {
            if (a.XFlag) flags |= CdlIndexMode8;
            if (a.MFlag) flags |= CdlMemoryMode8;
        }

        return flags;
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

    // ── CDL → RomAnnotationStore ──────────────────────────────────────────────

    // CDL flag byte constants (mirrors Mesen's CdlFlags enum).
    private const byte CdlFlagCode        = 0x01;
    private const byte CdlFlagData        = 0x02;
    private const byte CdlFlagSubEntry    = 0x08;
    private const byte CdlFlagIndexMode8  = 0x10;  // XFlag
    private const byte CdlFlagMemoryMode8 = 0x20;  // MFlag

    /// <summary>
    /// Merge Mesen CDL flag bytes into an existing <see cref="RomAnnotationStore"/>,
    /// returning a new store with updated annotations.
    ///
    /// Merge rules (additive — existing data is never downgraded):
    /// <list type="bullet">
    ///   <item>A byte previously <see cref="ByteType.Unreached"/> is promoted to
    ///         <see cref="ByteType.Opcode"/> (Code) or <see cref="ByteType.Data8"/>
    ///         (Data) based on the CDL flag.</item>
    ///   <item>M/X flags are OR-ed in — once set they are never cleared.</item>
    ///   <item><see cref="InOutPoint.InPoint"/> is OR-ed in when CDL reports
    ///         <c>SubEntryPoint</c>.</item>
    ///   <item>CDL bytes beyond the length of <see cref="RomAnnotationStore.Bytes"/>
    ///         are ignored.</item>
    /// </list>
    /// </summary>
    /// <param name="store">Existing annotation store to merge into.</param>
    /// <param name="cdlData">Raw CDL flag bytes from <c>DebugApi.SetCdlData</c>
    ///     (no file header — one byte per ROM byte).</param>
    public static RomAnnotationStore MergeFromCdlData(RomAnnotationStore store, byte[] cdlData)
    {
        var src   = store.Bytes;
        var count = Math.Min(src.Length, cdlData.Length);
        var dest  = new ByteAnnotation[src.Length];
        Array.Copy(src, dest, src.Length);

        for (var i = 0; i < count; i++)
        {
            byte cdl = cdlData[i];
            if (cdl == CdlNone) continue;

            var a = dest[i];

            // Promote Unreached bytes based on CDL code/data flag.
            if (a.Type == ByteType.Unreached)
            {
                if      ((cdl & CdlFlagCode) != 0) a = a with { Type = ByteType.Opcode };
                else if ((cdl & CdlFlagData) != 0) a = a with { Type = ByteType.Data8  };
            }

            // OR in M/X flags for code bytes.
            if ((cdl & CdlFlagCode) != 0)
            {
                if ((cdl & CdlFlagIndexMode8)  != 0) a = a with { XFlag = true };
                if ((cdl & CdlFlagMemoryMode8) != 0) a = a with { MFlag = true };
            }

            // OR in SubEntryPoint → InPoint.
            if ((cdl & CdlFlagSubEntry) != 0)
                a = a with { Flow = a.Flow | InOutPoint.InPoint };

            dest[i] = a;
        }

        return new RomAnnotationStore
        {
            RomGameName   = store.RomGameName,
            RomChecksum   = store.RomChecksum,
            MapMode       = store.MapMode,
            Speed         = store.Speed,
            SaveVersion   = store.SaveVersion,
            Bytes         = dest,
            Labels        = store.Labels,
            LabelComments = store.LabelComments,
            Comments      = store.Comments,
        };
    }
}
