namespace Mesen.Annotation.Diz;

/// <summary>
/// Encodes and decodes a single ROM byte annotation to/from DiztinGUIsh's
/// compact 9-character text format (trailing '0' characters stripped).
///
/// Wire format (positions 0–8):
///   [0]    FlagType char: U + . G M X T A B C D E F H
///   [1]    Fake64 char:   6-bit packed — bit2=XFlag, bit3=MFlag, bits4-7=InOutPoint
///   [2-3]  DataBank hex:  two uppercase hex digits
///   [4-7]  DirectPage hex: four uppercase hex digits
///   [8]    Architecture:  one hex digit (0=65C816, 1=SPC700, 2=SuperFX)
///
/// Identical to DiztinGUIsh's RomByteEncoding.cs — numeric values must not change.
/// </summary>
public static class DizByteLineCodec
{
    // ── Flag character table (identical to DiztinGUIsh's FlagEncodeTable) ────

    private static readonly Dictionary<char, ByteType> CharToType = new()
    {
        { 'U', ByteType.Unreached  },
        { '+', ByteType.Opcode     },
        { '.', ByteType.Operand    },
        { 'G', ByteType.Graphics   },
        { 'M', ByteType.Music      },
        { 'X', ByteType.Empty      },
        { 'T', ByteType.Text       },
        { 'A', ByteType.Data8      },
        { 'B', ByteType.Data16     },
        { 'C', ByteType.Data24     },
        { 'D', ByteType.Data32     },
        { 'E', ByteType.Pointer16  },
        { 'F', ByteType.Pointer24  },
        { 'H', ByteType.Pointer32  },
    };

    private static readonly Dictionary<ByteType, char> TypeToChar =
        CharToType.ToDictionary(kv => kv.Value, kv => kv.Key);

    private const int LineLength = 9;
    private static readonly char[] HexChars = "0123456789ABCDEF".ToCharArray();

    // ── Decode ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decode a single encoded line (1–9 chars; missing trailing chars are
    /// treated as '0') into a ByteAnnotation.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown flag character.</exception>
    public static ByteAnnotation Decode(ReadOnlySpan<char> line)
    {
        // pad to full 9 chars by treating missing chars as '0'
        Span<char> buf = stackalloc char[LineLength];
        buf.Fill('0');
        line.CopyTo(buf);

        if (!CharToType.TryGetValue(buf[0], out var type))
            throw new ArgumentException($"Unknown DiztinGUIsh flag character '{buf[0]}'.");

        var otherFlags = DizFake64.Decode(buf[1]);

        var xFlag    = ((otherFlags >> 2) & 0x1) != 0;
        var mFlag    = ((otherFlags >> 3) & 0x1) != 0;
        var flow     = (InOutPoint)((otherFlags >> 4) & 0xF);

        var dataBank   = (byte)(ParseHex1(buf[2]) << 4 | ParseHex1(buf[3]));
        var directPage = (ushort)(
            ParseHex1(buf[4]) << 12 |
            ParseHex1(buf[5]) <<  8 |
            ParseHex1(buf[6]) <<  4 |
            ParseHex1(buf[7]));
        var arch = (CpuArch)(ParseHex1(buf[8]) & 0x3);

        return new ByteAnnotation
        {
            Type       = type,
            MFlag      = mFlag,
            XFlag      = xFlag,
            DataBank   = dataBank,
            DirectPage = directPage,
            Flow       = flow,
            Arch       = arch,
        };
    }

    // ── Encode ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encode a ByteAnnotation into the compact Diz line format.
    /// Trailing '0' characters are stripped; result is at least 1 character.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown ByteType value.</exception>
    public static string Encode(ByteAnnotation a)
    {
        if (!TypeToChar.TryGetValue(a.Type, out var flagChar))
            throw new ArgumentException($"Unknown ByteType value {a.Type} — no DiztinGUIsh flag character mapping.");

        // pack XFlag (bit 2), MFlag (bit 3), InOutPoint (bits 4-7) into 6-bit value
        var otherFlags = (byte)(
            (a.XFlag ? 1 : 0) << 2 |
            (a.MFlag ? 1 : 0) << 3 |
            (byte)a.Flow      << 4
        );

        Span<char> buf = stackalloc char[LineLength];
        buf[0] = flagChar;
        buf[1] = DizFake64.Encode(otherFlags);
        buf[2] = HexChars[a.DataBank >> 4];
        buf[3] = HexChars[a.DataBank & 0xF];
        buf[4] = HexChars[(a.DirectPage >> 12) & 0xF];
        buf[5] = HexChars[(a.DirectPage >>  8) & 0xF];
        buf[6] = HexChars[(a.DirectPage >>  4) & 0xF];
        buf[7] = HexChars[ a.DirectPage        & 0xF];
        buf[8] = HexChars[(byte)a.Arch & 0xF];

        // strip trailing '0' characters (Diz's "light compression")
        var lastNonZero = LineLength - 1;
        while (lastNonZero > 0 && buf[lastNonZero] == '0')
            lastNonZero--;

        return new string(buf[..(lastNonZero + 1)]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int ParseHex1(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => throw new ArgumentException($"Invalid hex character '{c}'."),
    };
}
