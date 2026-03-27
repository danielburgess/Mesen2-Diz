using System.Text;

namespace Mesen.Annotation.Diz;

/// <summary>
/// Encodes a <see cref="ByteAnnotation"/> array into the raw text content
/// of a DiztinGUIsh &lt;RomBytes&gt; XML element.
///
/// Encoding stages (reverse of decode):
///   1. Per-byte encoding via <see cref="DizByteLineCodec.Encode"/>
///   2. RLE group-block compression (runs of ≥ 8 identical lines)
///   3. Table1 substitution (forward direction)
///
/// The table and RLE threshold are identical to DiztinGUIsh's
/// SubstitutionCompression and RepeaterCompression, so files written
/// here can be read by DiztinGUIsh unmodified.
/// </summary>
public static class DizRomBytesSectionEncoder
{
    /// <summary>
    /// Minimum consecutive identical lines before RLE is applied.
    /// Matches DiztinGUIsh's <c>minNumberRepeatsBeforeWeBother</c>.
    /// </summary>
    private const int RleMinRun = 8;

    // Substitution table — identical to DiztinGUIsh's SubstitutionCompression.Table1.
    // Encode applies entries in FORWARD order (index 0 first).
    private static readonly (string Long, string Short)[] SubstitutionTable =
    [
        ("0001E", "ZQ"),
        ("B0001", "Zq"),
        ("C0001", "ZX"),
        ("B7E",   "Zx"),
        ("07F01", "ZY"),
        ("0001D", "Zy"),
        ("C7E",   "ZZ"),
        ("07E",   "Zz"),
        ("00001", "ZS"),
        ("0001",  "Zs"),
    ];

    /// <summary>
    /// Encodes <paramref name="bytes"/> into the raw string that belongs
    /// inside a &lt;RomBytes&gt; element (including the leading newline,
    /// options header line, and trailing newline after the last data line).
    /// </summary>
    public static string Encode(ByteAnnotation[] bytes)
    {
        var lines = new List<string>(bytes.Length);
        foreach (var b in bytes)
            lines.Add(DizByteLineCodec.Encode(b));

        ApplyRle(lines);
        ApplyForwardSubstitution(lines);

        var sb = new StringBuilder(lines.Count * 4 + 64);
        sb.Append('\n');
        sb.Append("version:201,compress_groupblocks,compress_table_1");
        sb.Append('\n');
        foreach (var line in lines)
        {
            sb.Append(line);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── Stage 2: RLE ──────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces runs of ≥ <see cref="RleMinRun"/> identical lines with
    /// <c>r {count} {line}</c> entries. Mirrors DiztinGUIsh's
    /// <c>RepeaterCompression.Compress</c> exactly.
    /// </summary>
    private static void ApplyRle(List<string> lines)
    {
        if (lines.Count < RleMinRun)
            return;

        var output = new List<string>(lines.Count);
        var lastLine   = lines[0];
        var consecutive = 1;

        for (var i = 1; i < lines.Count; i++)
        {
            var line      = lines[i];
            var different = line != lastLine;
            var finalLine = i == lines.Count - 1;

            if (!different)
            {
                consecutive++;
                if (!finalLine) continue;
            }

            // Flush accumulated run of lastLine.
            if (consecutive >= RleMinRun)
                output.Add($"r {consecutive} {lastLine}");
            else
                for (var j = 0; j < consecutive; j++)
                    output.Add(lastLine);

            // If we hit the last line and it differs from the run, emit it too.
            if (finalLine && different)
                output.Add(line);

            lastLine    = line;
            consecutive = 1;
        }

        lines.Clear();
        lines.AddRange(output);
    }

    // ── Stage 3: Table1 substitution (forward) ────────────────────────────────

    private static void ApplyForwardSubstitution(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var (longPat, shortTok) in SubstitutionTable)
            {
                if (lines[i].Contains(longPat))
                    lines[i] = lines[i].Replace(longPat, shortTok);
            }
        }
    }
}
