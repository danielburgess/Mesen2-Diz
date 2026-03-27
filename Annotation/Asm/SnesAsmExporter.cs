using System.Text;

namespace Mesen.Annotation.Asm;

/// <summary>
/// Exports a <see cref="RomAnnotationStore"/> plus the raw ROM bytes to an
/// Asar-compatible 65816 assembly source file (.asm).
///
/// Each mapped, annotated byte in the ROM is emitted either as a disassembled
/// instruction (for Opcode bytes) or as a db/dw/dl/dd data directive (for Data
/// bytes). Unreached bytes are skipped and cause an org gap for whatever follows.
///
/// Labels are emitted above their target byte. Inline comments appear as
/// trailing ; remarks on the same line. Label-comments (DiztinGUIsh block
/// comments attached to a label) are emitted as ; lines above the label.
///
/// Branch and jump targets that have a label in the store are shown by name
/// rather than hex address.
/// </summary>
public static class SnesAsmExporter
{
    private const int CommentColumn    = 32;
    private const int DataLineMaxBytes = 16;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generate Asar-compatible 65816 .asm source text.
    /// </summary>
    /// <param name="store">Annotation metadata (types, flags, labels, comments).</param>
    /// <param name="romBytes">
    /// Raw ROM file bytes, one entry per ROM offset. Must be the same length as
    /// <paramref name="store"/>.Bytes.
    /// </param>
    public static string Export(RomAnnotationStore store, byte[] romBytes)
    {
        if (romBytes.Length != store.Bytes.Length)
            throw new ArgumentException(
                $"romBytes length {romBytes.Length} does not match " +
                $"store.Bytes length {store.Bytes.Length}.",
                nameof(romBytes));

        var sb = new StringBuilder(romBytes.Length * 6);
        EmitFileHeader(sb, store);

        int i = 0;
        int prevSnesAddr = -1;
        int prevSize     = 0;

        while (i < romBytes.Length)
        {
            var ann = store.Bytes[i];

            // Skip unreached bytes and stray Operand bytes (no preceding Opcode).
            // Reset prevSnesAddr so that whatever follows gets a fresh org.
            if (ann.Type == ByteType.Unreached || ann.Type == ByteType.Operand)
            {
                prevSnesAddr = -1;
                i++;
                continue;
            }

            if (!SnesAddressConverter.TryToSnesAddress(
                    i, romBytes.Length, store.MapMode, out int snesAddr))
            {
                i++;
                continue;
            }

            // Emit org when not contiguous with the previous output block.
            bool needsOrg = prevSnesAddr < 0 || snesAddr != prevSnesAddr + prevSize;
            if (needsOrg)
            {
                if (prevSnesAddr >= 0) sb.AppendLine();
                sb.AppendLine($"        org ${snesAddr:X6}");
                sb.AppendLine();
            }

            // Block comment attached to the label at this address.
            if (store.LabelComments.TryGetValue(snesAddr, out var block) && block.Length > 0)
            {
                foreach (var line in block.Split('\n'))
                    sb.AppendLine($"; {line.TrimEnd('\r')}");
            }

            // Label itself.
            if (store.Labels.TryGetValue(snesAddr, out var label) && label.Length > 0)
                sb.AppendLine($"{label}:");

            store.Comments.TryGetValue(snesAddr, out var inlineComment);

            int size;
            if (ann.Type == ByteType.Opcode)
                size = EmitInstruction(sb, store, romBytes, i, snesAddr, ann, inlineComment);
            else
                size = EmitDataGroup(sb, store, romBytes, i, snesAddr, ann.Type, inlineComment);

            prevSnesAddr = snesAddr;
            prevSize     = size;
            i           += size;
        }

        return sb.ToString();
    }

    // ── File header ───────────────────────────────────────────────────────────

    private static void EmitFileHeader(StringBuilder sb, RomAnnotationStore store)
    {
        sb.AppendLine($"; {store.RomGameName}");
        sb.AppendLine($"; Map: {store.MapMode} | {store.Speed}");
        sb.AppendLine($"; Checksum: ${store.RomChecksum:X8}");
        sb.AppendLine($"; Exported by Mesen2-Diz");
        sb.AppendLine();
    }

    // ── Code instruction emit ─────────────────────────────────────────────────

    private static int EmitInstruction(
        StringBuilder sb,
        RomAnnotationStore store,
        byte[] romBytes,
        int romOff,
        int snesAddr,
        ByteAnnotation ann,
        string? comment)
    {
        byte opcode = romBytes[romOff];
        var info    = Table[opcode];
        int numOps  = OperandBytes(info.Mode, ann.MFlag, ann.XFlag);

        // Collect operand bytes (zero-padded if the ROM is unexpectedly short).
        var ops = new byte[numOps];
        for (int k = 0; k < numOps && romOff + 1 + k < romBytes.Length; k++)
            ops[k] = romBytes[romOff + 1 + k];

        string operand = FormatOperand(info.Mode, ops, opcode, snesAddr, ann, store);

        var line = new StringBuilder("        ");
        line.Append(info.Mnemonic);
        if (operand.Length > 0) { line.Append(' '); line.Append(operand); }
        AppendComment(line, comment);

        sb.AppendLine(line.ToString());
        return 1 + numOps;
    }

    // ── Data group emit ───────────────────────────────────────────────────────

    private static int EmitDataGroup(
        StringBuilder sb,
        RomAnnotationStore store,
        byte[] romBytes,
        int startRom,
        int startSnes,
        ByteType type,
        string? firstComment)
    {
        int elemBytes = ElemBytes(type);
        string dir    = elemBytes switch { 2 => "dw", 3 => "dl", 4 => "dd", _ => "db" };

        int romLen = romBytes.Length;
        int i      = startRom;
        var elems  = new List<string>(DataLineMaxBytes / elemBytes + 1);
        string? pendingComment = firstComment;

        while (i < romLen)
        {
            if (store.Bytes[i].Type != type) break;

            if (!SnesAddressConverter.TryToSnesAddress(
                    i, romLen, store.MapMode, out int addr)) break;

            // A label, label-comment, or inline comment at a non-first byte
            // ends the group so annotations can be re-emitted on the new line.
            if (i != startRom)
            {
                if (store.Labels.ContainsKey(addr))       break;
                if (store.Comments.ContainsKey(addr))     break;
                if (store.LabelComments.ContainsKey(addr)) break;
            }

            if (i + elemBytes > romLen) break;

            ulong val = 0;
            for (int k = 0; k < elemBytes; k++)
                val |= (ulong)romBytes[i + k] << (8 * k);

            elems.Add(elemBytes switch
            {
                2 => $"${val:X4}",
                3 => $"${val:X6}",
                4 => $"${val:X8}",
                _ => $"${val:X2}",
            });
            i += elemBytes;

            if (elems.Count * elemBytes >= DataLineMaxBytes)
            {
                FlushDataLine(sb, dir, elems, pendingComment);
                elems.Clear();
                pendingComment = null;
            }
        }

        if (elems.Count > 0)
            FlushDataLine(sb, dir, elems, pendingComment);

        // Guarantee forward progress even if nothing was emitted.
        return Math.Max(i - startRom, 1);
    }

    private static void FlushDataLine(
        StringBuilder sb, string dir, List<string> elems, string? comment)
    {
        var line = new StringBuilder("        ");
        line.Append(dir);
        line.Append(' ');
        line.Append(string.Join(",", elems));
        AppendComment(line, comment);
        sb.AppendLine(line.ToString());
    }

    // ── Operand formatting ────────────────────────────────────────────────────

    private static string FormatOperand(
        AddrMode mode,
        byte[] ops,
        byte opcode,
        int snesAddr,
        ByteAnnotation ann,
        RomAnnotationStore store)
    {
        return mode switch
        {
            AddrMode.Imp     => "",
            AddrMode.Acc     => "A",

            AddrMode.ImmM    => ops.Length >= 2 ? $"#${U16(ops):X4}" : $"#${ops[0]:X2}",
            AddrMode.ImmX    => ops.Length >= 2 ? $"#${U16(ops):X4}" : $"#${ops[0]:X2}",
            AddrMode.Imm8    => $"#${ops[0]:X2}",

            AddrMode.Dp      => $"${ops[0]:X2}",
            AddrMode.DpX     => $"${ops[0]:X2},X",
            AddrMode.DpY     => $"${ops[0]:X2},Y",
            AddrMode.DpInd   => $"(${ops[0]:X2})",
            AddrMode.DpIndX  => $"(${ops[0]:X2},X)",
            AddrMode.DpIndY  => $"(${ops[0]:X2}),Y",
            AddrMode.DpIndL  => $"[${ops[0]:X2}]",
            AddrMode.DpIndLY => $"[${ops[0]:X2}],Y",
            AddrMode.Sr      => $"${ops[0]:X2},S",
            AddrMode.SrIndY  => $"(${ops[0]:X2},S),Y",
            AddrMode.PEI     => $"(${ops[0]:X2})",

            AddrMode.Abs =>
                TryLabel(store, AbsTarget(ops, opcode, snesAddr, ann)) ?? $"${U16(ops):X4}",
            AddrMode.AbsX =>
                TryLabel(store, (ann.DataBank << 16) | U16(ops)) is { } lx
                    ? $"{lx},X" : $"${U16(ops):X4},X",
            AddrMode.AbsY =>
                TryLabel(store, (ann.DataBank << 16) | U16(ops)) is { } ly
                    ? $"{ly},Y" : $"${U16(ops):X4},Y",
            AddrMode.AbsInd  => $"(${U16(ops):X4})",
            AddrMode.AbsIndX => $"(${U16(ops):X4},X)",
            AddrMode.AbsIndL => $"[${U16(ops):X4}]",

            AddrMode.AbsL =>
                TryLabel(store, U24(ops)) ?? $"${U24(ops):X6}",
            AddrMode.AbsLX =>
                TryLabel(store, U24(ops)) is { } ll ? $"{ll},X" : $"${U24(ops):X6},X",

            AddrMode.Rel8 =>
                FormatRel8(ops[0], snesAddr, store),
            AddrMode.Rel16 =>
                FormatRel16(U16(ops), snesAddr, store),

            // MVN/MVP: ROM encoding is [opcode][srcbk][dstbk]; mnemonic is src,dst.
            AddrMode.Blk => $"${ops[0]:X2},${ops[1]:X2}",

            // PEA: push 2-byte literal. Format as $XXXX (no #) without label lookup.
            AddrMode.Pea => $"${U16(ops):X4}",

            _ => "",
        };
    }

    /// <summary>
    /// Resolve the target SNES address for an Abs-mode operand, taking into
    /// account whether the opcode refers to the code bank or the data bank.
    /// </summary>
    private static int AbsTarget(byte[] ops, byte opcode, int snesAddr, ByteAnnotation ann)
    {
        // JSR ($20), JMP abs ($4C), and PEA ($F4) reference the current code bank.
        // Everything else uses the data bank register (DB / ann.DataBank).
        bool useCodeBank = opcode == 0x20 || opcode == 0x4C || opcode == 0xF4;
        int bank = useCodeBank ? (snesAddr >> 16) & 0xFF : ann.DataBank;
        return (bank << 16) | U16(ops);
    }

    private static string FormatRel8(byte b, int snesAddr, RomAnnotationStore store)
    {
        int target = (snesAddr & 0xFF0000) | ((snesAddr + 2 + (sbyte)b) & 0xFFFF);
        return TryLabel(store, target) ?? $"${target:X6}";
    }

    private static string FormatRel16(int w, int snesAddr, RomAnnotationStore store)
    {
        int target = (snesAddr & 0xFF0000) | ((snesAddr + 3 + (short)w) & 0xFFFF);
        return TryLabel(store, target) ?? $"${target:X6}";
    }

    private static string? TryLabel(RomAnnotationStore store, int snesAddr) =>
        store.Labels.TryGetValue(snesAddr, out var n) && n.Length > 0 ? n : null;

    // ── Misc helpers ──────────────────────────────────────────────────────────

    private static void AppendComment(StringBuilder line, string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return;
        int padTo = Math.Max(line.Length + 1, CommentColumn);
        while (line.Length < padTo) line.Append(' ');
        line.Append("; ");
        line.Append(comment.Replace("\n", " "));
    }

    private static int ElemBytes(ByteType type) => type switch
    {
        ByteType.Data16 or ByteType.Pointer16 => 2,
        ByteType.Data24 or ByteType.Pointer24 => 3,
        ByteType.Data32 or ByteType.Pointer32 => 4,
        _                                     => 1,
    };

    private static ushort U16(byte[] ops) =>
        (ushort)((ops.Length > 0 ? ops[0] : 0) | (ops.Length > 1 ? ops[1] << 8 : 0));

    private static int U24(byte[] ops) =>
        (ops.Length > 0 ? ops[0] : 0)        |
        (ops.Length > 1 ? ops[1] << 8  : 0)  |
        (ops.Length > 2 ? ops[2] << 16 : 0);

    // ── Operand byte counts by addressing mode ────────────────────────────────

    private static int OperandBytes(AddrMode mode, bool m, bool x) => mode switch
    {
        AddrMode.Imp or AddrMode.Acc => 0,

        AddrMode.ImmM => m ? 1 : 2,
        AddrMode.ImmX => x ? 1 : 2,

        AddrMode.Imm8   or
        AddrMode.Dp     or AddrMode.DpX     or AddrMode.DpY    or
        AddrMode.DpInd  or AddrMode.DpIndX  or AddrMode.DpIndY or
        AddrMode.DpIndL or AddrMode.DpIndLY or
        AddrMode.Sr     or AddrMode.SrIndY  or
        AddrMode.Rel8   or AddrMode.PEI    => 1,

        AddrMode.Abs    or AddrMode.AbsX   or AddrMode.AbsY    or
        AddrMode.AbsInd or AddrMode.AbsIndX or AddrMode.AbsIndL or
        AddrMode.Rel16  or AddrMode.Blk    or AddrMode.Pea    => 2,

        AddrMode.AbsL or AddrMode.AbsLX => 3,

        _ => 0,
    };

    // ── Addressing mode enum ──────────────────────────────────────────────────

    private enum AddrMode : byte
    {
        Imp,
        Acc,
        ImmM,
        ImmX,
        Imm8,
        Dp,
        DpX,
        DpY,
        DpInd,
        DpIndX,
        DpIndY,
        DpIndL,
        DpIndLY,
        Sr,
        SrIndY,
        PEI,
        Abs,
        AbsX,
        AbsY,
        AbsInd,
        AbsIndX,
        AbsIndL,
        AbsL,
        AbsLX,
        Rel8,
        Rel16,
        Blk,
        Pea,
    }

    // ── Opcode info ───────────────────────────────────────────────────────────

    private readonly record struct OpcodeInfo(string Mnemonic, AddrMode Mode);

    // ── 65816 opcode table ────────────────────────────────────────────────────
    //
    // 256 entries, indexed by opcode byte (00–FF).
    // Numeric values of AddrMode enum must never be used as a wire format.

    private static readonly OpcodeInfo[] Table =
    [
        // 00–0F
        new("BRK", AddrMode.Imm8),   new("ORA", AddrMode.DpIndX),  new("COP", AddrMode.Imm8),   new("ORA", AddrMode.Sr),
        new("TSB", AddrMode.Dp),     new("ORA", AddrMode.Dp),      new("ASL", AddrMode.Dp),      new("ORA", AddrMode.DpIndL),
        new("PHP", AddrMode.Imp),    new("ORA", AddrMode.ImmM),    new("ASL", AddrMode.Acc),     new("PHD", AddrMode.Imp),
        new("TSB", AddrMode.Abs),    new("ORA", AddrMode.Abs),     new("ASL", AddrMode.Abs),     new("ORA", AddrMode.AbsL),
        // 10–1F
        new("BPL", AddrMode.Rel8),   new("ORA", AddrMode.DpIndY),  new("ORA", AddrMode.DpInd),   new("ORA", AddrMode.SrIndY),
        new("TRB", AddrMode.Dp),     new("ORA", AddrMode.DpX),     new("ASL", AddrMode.DpX),     new("ORA", AddrMode.DpIndLY),
        new("CLC", AddrMode.Imp),    new("ORA", AddrMode.AbsY),    new("INC", AddrMode.Acc),     new("TCS", AddrMode.Imp),
        new("TRB", AddrMode.Abs),    new("ORA", AddrMode.AbsX),    new("ASL", AddrMode.AbsX),    new("ORA", AddrMode.AbsLX),
        // 20–2F
        new("JSR", AddrMode.Abs),    new("AND", AddrMode.DpIndX),  new("JSL", AddrMode.AbsL),    new("AND", AddrMode.Sr),
        new("BIT", AddrMode.Dp),     new("AND", AddrMode.Dp),      new("ROL", AddrMode.Dp),      new("AND", AddrMode.DpIndL),
        new("PLP", AddrMode.Imp),    new("AND", AddrMode.ImmM),    new("ROL", AddrMode.Acc),     new("PLD", AddrMode.Imp),
        new("BIT", AddrMode.Abs),    new("AND", AddrMode.Abs),     new("ROL", AddrMode.Abs),     new("AND", AddrMode.AbsL),
        // 30–3F
        new("BMI", AddrMode.Rel8),   new("AND", AddrMode.DpIndY),  new("AND", AddrMode.DpInd),   new("AND", AddrMode.SrIndY),
        new("BIT", AddrMode.DpX),    new("AND", AddrMode.DpX),     new("ROL", AddrMode.DpX),     new("AND", AddrMode.DpIndLY),
        new("SEC", AddrMode.Imp),    new("AND", AddrMode.AbsY),    new("DEC", AddrMode.Acc),     new("TSC", AddrMode.Imp),
        new("BIT", AddrMode.AbsX),   new("AND", AddrMode.AbsX),    new("ROL", AddrMode.AbsX),    new("AND", AddrMode.AbsLX),
        // 40–4F
        new("RTI", AddrMode.Imp),    new("EOR", AddrMode.DpIndX),  new("WDM", AddrMode.Imm8),    new("EOR", AddrMode.Sr),
        new("MVP", AddrMode.Blk),    new("EOR", AddrMode.Dp),      new("LSR", AddrMode.Dp),      new("EOR", AddrMode.DpIndL),
        new("PHA", AddrMode.Imp),    new("EOR", AddrMode.ImmM),    new("LSR", AddrMode.Acc),     new("PHK", AddrMode.Imp),
        new("JMP", AddrMode.Abs),    new("EOR", AddrMode.Abs),     new("LSR", AddrMode.Abs),     new("EOR", AddrMode.AbsL),
        // 50–5F
        new("BVC", AddrMode.Rel8),   new("EOR", AddrMode.DpIndY),  new("EOR", AddrMode.DpInd),   new("EOR", AddrMode.SrIndY),
        new("MVN", AddrMode.Blk),    new("EOR", AddrMode.DpX),     new("LSR", AddrMode.DpX),     new("EOR", AddrMode.DpIndLY),
        new("CLI", AddrMode.Imp),    new("EOR", AddrMode.AbsY),    new("PHY", AddrMode.Imp),     new("TCD", AddrMode.Imp),
        new("JML", AddrMode.AbsL),   new("EOR", AddrMode.AbsX),    new("LSR", AddrMode.AbsX),    new("EOR", AddrMode.AbsLX),
        // 60–6F
        new("RTS", AddrMode.Imp),    new("ADC", AddrMode.DpIndX),  new("PER", AddrMode.Rel16),   new("ADC", AddrMode.Sr),
        new("STZ", AddrMode.Dp),     new("ADC", AddrMode.Dp),      new("ROR", AddrMode.Dp),      new("ADC", AddrMode.DpIndL),
        new("PLA", AddrMode.Imp),    new("ADC", AddrMode.ImmM),    new("ROR", AddrMode.Acc),     new("RTL", AddrMode.Imp),
        new("JMP", AddrMode.AbsInd), new("ADC", AddrMode.Abs),     new("ROR", AddrMode.Abs),     new("ADC", AddrMode.AbsL),
        // 70–7F
        new("BVS", AddrMode.Rel8),   new("ADC", AddrMode.DpIndY),  new("ADC", AddrMode.DpInd),   new("ADC", AddrMode.SrIndY),
        new("STZ", AddrMode.DpX),    new("ADC", AddrMode.DpX),     new("ROR", AddrMode.DpX),     new("ADC", AddrMode.DpIndLY),
        new("SEI", AddrMode.Imp),    new("ADC", AddrMode.AbsY),    new("PLY", AddrMode.Imp),     new("TDC", AddrMode.Imp),
        new("JMP", AddrMode.AbsIndX),new("ADC", AddrMode.AbsX),    new("ROR", AddrMode.AbsX),    new("ADC", AddrMode.AbsLX),
        // 80–8F
        new("BRA", AddrMode.Rel8),   new("STA", AddrMode.DpIndX),  new("BRL", AddrMode.Rel16),   new("STA", AddrMode.Sr),
        new("STY", AddrMode.Dp),     new("STA", AddrMode.Dp),      new("STX", AddrMode.Dp),      new("STA", AddrMode.DpIndL),
        new("DEY", AddrMode.Imp),    new("BIT", AddrMode.ImmM),    new("TXA", AddrMode.Imp),     new("PHB", AddrMode.Imp),
        new("STY", AddrMode.Abs),    new("STA", AddrMode.Abs),     new("STX", AddrMode.Abs),     new("STA", AddrMode.AbsL),
        // 90–9F
        new("BCC", AddrMode.Rel8),   new("STA", AddrMode.DpIndY),  new("STA", AddrMode.DpInd),   new("STA", AddrMode.SrIndY),
        new("STY", AddrMode.DpX),    new("STA", AddrMode.DpX),     new("STX", AddrMode.DpY),     new("STA", AddrMode.DpIndLY),
        new("TYA", AddrMode.Imp),    new("STA", AddrMode.AbsY),    new("TXS", AddrMode.Imp),     new("TXY", AddrMode.Imp),
        new("STZ", AddrMode.Abs),    new("STA", AddrMode.AbsX),    new("STZ", AddrMode.AbsX),    new("STA", AddrMode.AbsLX),
        // A0–AF
        new("LDY", AddrMode.ImmX),   new("LDA", AddrMode.DpIndX),  new("LDX", AddrMode.ImmX),    new("LDA", AddrMode.Sr),
        new("LDY", AddrMode.Dp),     new("LDA", AddrMode.Dp),      new("LDX", AddrMode.Dp),      new("LDA", AddrMode.DpIndL),
        new("TAY", AddrMode.Imp),    new("LDA", AddrMode.ImmM),    new("TAX", AddrMode.Imp),     new("PLB", AddrMode.Imp),
        new("LDY", AddrMode.Abs),    new("LDA", AddrMode.Abs),     new("LDX", AddrMode.Abs),     new("LDA", AddrMode.AbsL),
        // B0–BF
        new("BCS", AddrMode.Rel8),   new("LDA", AddrMode.DpIndY),  new("LDA", AddrMode.DpInd),   new("LDA", AddrMode.SrIndY),
        new("LDY", AddrMode.DpX),    new("LDA", AddrMode.DpX),     new("LDX", AddrMode.DpY),     new("LDA", AddrMode.DpIndLY),
        new("CLV", AddrMode.Imp),    new("LDA", AddrMode.AbsY),    new("TSX", AddrMode.Imp),     new("TYX", AddrMode.Imp),
        new("LDY", AddrMode.AbsX),   new("LDA", AddrMode.AbsX),    new("LDX", AddrMode.AbsY),    new("LDA", AddrMode.AbsLX),
        // C0–CF
        new("CPY", AddrMode.ImmX),   new("CMP", AddrMode.DpIndX),  new("REP", AddrMode.Imm8),    new("CMP", AddrMode.Sr),
        new("CPY", AddrMode.Dp),     new("CMP", AddrMode.Dp),      new("DEC", AddrMode.Dp),      new("CMP", AddrMode.DpIndL),
        new("INY", AddrMode.Imp),    new("CMP", AddrMode.ImmM),    new("DEX", AddrMode.Imp),     new("WAI", AddrMode.Imp),
        new("CPY", AddrMode.Abs),    new("CMP", AddrMode.Abs),     new("DEC", AddrMode.Abs),     new("CMP", AddrMode.AbsL),
        // D0–DF
        new("BNE", AddrMode.Rel8),   new("CMP", AddrMode.DpIndY),  new("CMP", AddrMode.DpInd),   new("CMP", AddrMode.SrIndY),
        new("PEI", AddrMode.PEI),    new("CMP", AddrMode.DpX),     new("DEC", AddrMode.DpX),     new("CMP", AddrMode.DpIndLY),
        new("CLD", AddrMode.Imp),    new("CMP", AddrMode.AbsY),    new("PHX", AddrMode.Imp),     new("STP", AddrMode.Imp),
        new("JML", AddrMode.AbsIndL),new("CMP", AddrMode.AbsX),    new("DEC", AddrMode.AbsX),    new("CMP", AddrMode.AbsLX),
        // E0–EF
        new("CPX", AddrMode.ImmX),   new("SBC", AddrMode.DpIndX),  new("SEP", AddrMode.Imm8),    new("SBC", AddrMode.Sr),
        new("CPX", AddrMode.Dp),     new("SBC", AddrMode.Dp),      new("INC", AddrMode.Dp),      new("SBC", AddrMode.DpIndL),
        new("INX", AddrMode.Imp),    new("SBC", AddrMode.ImmM),    new("NOP", AddrMode.Imp),     new("XBA", AddrMode.Imp),
        new("CPX", AddrMode.Abs),    new("SBC", AddrMode.Abs),     new("INC", AddrMode.Abs),     new("SBC", AddrMode.AbsL),
        // F0–FF
        new("BEQ", AddrMode.Rel8),   new("SBC", AddrMode.DpIndY),  new("SBC", AddrMode.DpInd),   new("SBC", AddrMode.SrIndY),
        new("PEA", AddrMode.Pea),    new("SBC", AddrMode.DpX),     new("INC", AddrMode.DpX),     new("SBC", AddrMode.DpIndLY),
        new("SED", AddrMode.Imp),    new("SBC", AddrMode.AbsY),    new("PLX", AddrMode.Imp),     new("XCE", AddrMode.Imp),
        new("JSR", AddrMode.AbsIndX),new("SBC", AddrMode.AbsX),    new("INC", AddrMode.AbsX),    new("SBC", AddrMode.AbsLX),
    ];
}
