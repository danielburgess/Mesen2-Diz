namespace Mesen.Annotation.Diz;

/// <summary>
/// Decodes the raw text content of a DiztinGUIsh &lt;RomBytes&gt; XML element into
/// an array of ByteAnnotations, one per ROM byte.
///
/// Handles all three in-file transformations in the correct decode order
/// (reverse of encode order):
///   1. Substitution (compress_table_1) — reverse string substitutions
///   2. Run-length expansion (compress_groupblocks) — expand "r N line" entries
///   3. Per-line decode — DizByteLineCodec.Decode() on each resulting line
///
/// Version history supported: 200 (trivial migration to 201) and 201.
/// </summary>
public static class DizRomBytesDecoder
{
    private const int MinSupportedVersion = 200;
    private const int MaxSupportedVersion = 201;
    private const int RleMinRun = 8; // matches DiztinGUIsh's minNumberRepeatsBeforeWeBother

    // Substitution table — identical to DiztinGUIsh's SubstitutionCompression.Table1.
    // Order matters: decode applies entries in REVERSE order.
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
    /// Parse the raw string content of a &lt;RomBytes&gt; element and return one
    /// ByteAnnotation per ROM byte, in ROM order.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown for unsupported versions, malformed RLE entries, or unknown flag chars.
    /// </exception>
    public static ByteAnnotation[] Decode(string sectionContent)
    {
        var (lines, useTable1, useGroupBlocks) = ParseHeader(sectionContent);

        // Decode order is the reverse of encode order.
        if (useTable1)
            ApplyReverseSubstitution(lines);

        if (useGroupBlocks)
            ExpandRle(lines);

        StripCommentsAndBlanks(lines);

        var result = new ByteAnnotation[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            result[i] = DizByteLineCodec.Decode(lines[i]);

        return result;
    }

    // ── Header parsing ────────────────────────────────────────────────────────

    private static (List<string> lines, bool useTable1, bool useGroupBlocks)
        ParseHeader(string content)
    {
        // Split on the first two newlines to isolate: blank | options | data body
        var parts = content.Split('\n', 3);
        if (parts.Length < 3)
            throw new InvalidDataException("RomBytes section is missing header lines.");

        var optionLine = parts[1];
        var dataBody   = parts[2];

        var options = optionLine.Split(',');
        CheckVersion(options);

        var useTable1     = options.Contains("compress_table_1");
        var useGroupBlocks = options.Contains("compress_groupblocks");

        var lines = dataBody.Split('\n').ToList();
        // remove trailing empty line produced by the final \n
        if (lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);

        return (lines, useTable1, useGroupBlocks);
    }

    private static void CheckVersion(string[] options)
    {
        var versionEntry = Array.Find(options, o => o.StartsWith("version:"));
        if (versionEntry is null)
            throw new InvalidDataException("RomBytes section is missing 'version:' in options.");

        var raw = versionEntry["version:".Length..];
        if (!int.TryParse(raw, out var version))
            throw new InvalidDataException($"Could not parse RomBytes version number: '{raw}'.");

        if (version > MaxSupportedVersion)
            throw new InvalidDataException(
                $"RomBytes section version {version} is newer than supported (max {MaxSupportedVersion}).");

        if (version < MinSupportedVersion)
            throw new InvalidDataException(
                $"RomBytes section version {version} is too old to read (min {MinSupportedVersion}).");

        // v200 → v201: no data changes needed (v201 simply allows inline ; comments).
    }

    // ── Decompression steps ───────────────────────────────────────────────────

    /// <summary>
    /// Reverse the substitution table (apply entries in reverse order, replacing
    /// short tokens with original long patterns).
    /// </summary>
    private static void ApplyReverseSubstitution(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            // iterate in reverse — must mirror DiztinGUIsh's decode direction
            for (var t = SubstitutionTable.Length - 1; t >= 0; t--)
            {
                var (longPat, shortTok) = SubstitutionTable[t];
                if (lines[i].Contains(shortTok))
                    lines[i] = lines[i].Replace(shortTok, longPat);
            }
        }
    }

    /// <summary>
    /// Expand run-length encoded entries of the form "r &lt;count&gt; &lt;line&gt;".
    /// </summary>
    private static void ExpandRle(List<string> lines)
    {
        var output = new List<string>(capacity: lines.Count);
        foreach (var line in lines)
        {
            if (!line.StartsWith("r "))
            {
                output.Add(line);
                continue;
            }

            var parts = line.Split(' ');
            if (parts.Length != 3)
                throw new InvalidDataException($"Malformed RLE entry: '{line}'.");

            if (!int.TryParse(parts[1], out var count) || count < 1)
                throw new InvalidDataException($"Invalid RLE count in: '{line}'.");

            for (var i = 0; i < count; i++)
                output.Add(parts[2]);
        }

        lines.Clear();
        lines.AddRange(output);
    }

    /// <summary>Strip inline ; comments and remove blank/whitespace-only lines.</summary>
    private static void StripCommentsAndBlanks(List<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            var semi = line.IndexOf(';');
            if (semi >= 0)
                line = line[..semi].TrimEnd();
            lines[i] = line;

            if (string.IsNullOrWhiteSpace(lines[i]))
                lines.RemoveAt(i);
        }
    }
}
