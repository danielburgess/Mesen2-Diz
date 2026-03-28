using Xunit;
using Mesen.Annotation;
using Mesen.Annotation.Asm;

namespace Mesen.Tests.Annotation.Asm;

public class SnesAsmExporterTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RomAnnotationStore MakeStore(
        ByteAnnotation[] bytes,
        RomMapMode map = RomMapMode.LoRom,
        Dictionary<int, string>? labels        = null,
        Dictionary<int, string>? labelComments = null,
        Dictionary<int, string>? comments      = null) =>
        new()
        {
            RomGameName   = "TEST GAME",
            RomChecksum   = 0xDEADBEEFu,
            MapMode       = map,
            Speed         = RomSpeed.SlowRom,
            SaveVersion   = 104,
            Bytes         = bytes,
            Labels        = labels        ?? new Dictionary<int, string>(),
            LabelComments = labelComments ?? new Dictionary<int, string>(),
            Comments      = comments      ?? new Dictionary<int, string>(),
        };

    // LoRom offset 0 → SNES $008000.
    private const int LoRomBase = 0x008000;

    private static ByteAnnotation Op(bool m = true, bool x = true, byte db = 0x00) =>
        new() { Type = ByteType.Opcode, MFlag = m, XFlag = x, DataBank = db };

    private static ByteAnnotation Opr() =>
        new() { Type = ByteType.Operand };

    private static ByteAnnotation D8()  => new() { Type = ByteType.Data8 };
    private static ByteAnnotation D16() => new() { Type = ByteType.Data16 };
    private static ByteAnnotation D24() => new() { Type = ByteType.Data24 };
    private static ByteAnnotation D32() => new() { Type = ByteType.Data32 };

    // ══════════════════════════════════════════════════════════════════════════
    // Argument validation
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_MismatchedLengths_Throws()
    {
        var store = MakeStore([Op()]);
        Assert.Throws<ArgumentException>(() =>
            SnesAsmExporter.Export(store, [0xEA, 0xEA]));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // File header
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Header_ContainsGameName()
    {
        var result = SnesAsmExporter.Export(MakeStore([]), []);
        Assert.Contains("TEST GAME", result);
    }

    [Fact]
    public void Export_Header_ContainsMapMode()
    {
        var result = SnesAsmExporter.Export(MakeStore([]), []);
        Assert.Contains("LoRom", result);
    }

    [Fact]
    public void Export_Header_ContainsChecksum()
    {
        var result = SnesAsmExporter.Export(MakeStore([]), []);
        Assert.Contains("DEADBEEF", result);
    }

    [Fact]
    public void Export_Header_ContainsLoRomDirective()
    {
        var result = SnesAsmExporter.Export(MakeStore([]), []);
        Assert.Contains("lorom", result);
    }

    [Fact]
    public void Export_Header_HiRomDirective()
    {
        var result = SnesAsmExporter.Export(MakeStore([], map: RomMapMode.HiRom), []);
        Assert.Contains("hirom", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Org directives — upper-bank convention ($80xxxx for LoRom)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_FirstByte_EmitsOrg()
    {
        // LoRom offset 0 → canonical $008000 → upper-bank org $808000.
        var result = SnesAsmExporter.Export(MakeStore([Op()]), [0xEA]);
        Assert.Contains("org $808000", result);
    }

    [Fact]
    public void Export_BankBoundary_EmitsNewOrg()
    {
        // LoRom bank 0: offsets 0..0x7FFF → SNES $808000–$80FFFF.
        // LoRom bank 1: offsets 0x8000..0xFFFF → SNES $818000–$81FFFF.
        // The gap $810000–$817FFF in SNES space triggers a fresh org for bank 1.
        int bankSize = 0x8000;
        var ann = new ByteAnnotation[bankSize + 1];
        for (int i = 0; i < bankSize; i++)
            ann[i] = new ByteAnnotation { Type = ByteType.Unreached };
        ann[bankSize] = Op();

        var rom = new byte[bankSize + 1];
        rom[bankSize] = 0xEA;

        var result = SnesAsmExporter.Export(MakeStore(ann), rom);
        Assert.Contains("org $818000", result);
    }

    [Fact]
    public void Export_Unreached_BridgesGap_NoExtraOrg()
    {
        // Offset 0 (NOP), offset 1 (Unreached → db $00), offset 2 (NOP).
        // All three bytes are contiguous in SNES address space → only one org.
        var ann = new ByteAnnotation[] { Op(), new() { Type = ByteType.Unreached }, Op() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0xEA, 0x00, 0xEA]);
        Assert.Contains("org $808000", result);
        var orgLines = result.Split('\n').Where(l => l.Contains("org $")).ToList();
        Assert.Single(orgLines);
    }

    [Fact]
    public void Export_Unreached_EmittedAsDb()
    {
        // Unreached bytes must appear as db directives, not be silently skipped.
        var result = SnesAsmExporter.Export(
            MakeStore([new ByteAnnotation { Type = ByteType.Unreached }]), [0xFF]);
        Assert.Contains("db $FF", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Labels and comments
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Label_EmittedBeforeInstruction()
    {
        var store = MakeStore([Op()],
            labels: new() { [LoRomBase] = "Init" });

        var result  = SnesAsmExporter.Export(store, [0xEA]);
        int labelPos = result.IndexOf("Init:", StringComparison.Ordinal);
        int nopPos   = result.IndexOf("NOP",   StringComparison.Ordinal);

        Assert.True(labelPos >= 0, "Label 'Init:' not found");
        Assert.True(labelPos < nopPos, "Label must precede NOP");
    }

    [Fact]
    public void Export_LabelComment_EmittedBeforeLabel()
    {
        var store = MakeStore([Op()],
            labels:        new() { [LoRomBase] = "Init" },
            labelComments: new() { [LoRomBase] = "Entry point" });

        var result      = SnesAsmExporter.Export(store, [0xEA]);
        int commentPos  = result.IndexOf("; Entry point", StringComparison.Ordinal);
        int labelPos    = result.IndexOf("Init:",          StringComparison.Ordinal);

        Assert.True(commentPos >= 0, "Block comment not found");
        Assert.True(commentPos < labelPos, "Block comment must precede label");
    }

    [Fact]
    public void Export_InlineComment_AppearsOnNopLine()
    {
        var store = MakeStore([Op()],
            comments: new() { [LoRomBase] = "no-op" });

        var result  = SnesAsmExporter.Export(store, [0xEA]);
        var nopLine = result.Split('\n').FirstOrDefault(l => l.Contains("NOP"));

        Assert.NotNull(nopLine);
        Assert.Contains("; no-op", nopLine);
    }

    [Fact]
    public void Export_MultilineBlockComment_EachLineHasSemicolon()
    {
        var store = MakeStore([Op()],
            labels:        new() { [LoRomBase] = "Foo" },
            labelComments: new() { [LoRomBase] = "line one\nline two" });

        var result = SnesAsmExporter.Export(store, [0xEA]);
        Assert.Contains("; line one", result);
        Assert.Contains("; line two", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Label lookup via ROM offset (mirror-safe)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Label_UpperBankMirror_ResolvedCorrectly()
    {
        // Label stored at upper-bank $808000 (DiztinGUIsh convention).
        // Should still be found for the byte at canonical $008000 (ROM offset 0).
        var store = MakeStore([Op()],
            labels: new() { [0x808000] = "UpperBankLabel" });

        var result = SnesAsmExporter.Export(store, [0xEA]);
        Assert.Contains("UpperBankLabel:", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Non-ROM label assignments
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_NonRomLabel_EmittedAsAssignment()
    {
        // $002116 is a hardware register (not in ROM for LoRom).
        var store = MakeStore([Op()],
            labels: new() { [0x002116] = "SNES_VMADDL" });

        var result = SnesAsmExporter.Export(store, [0xEA]);
        Assert.Contains("SNES_VMADDL = $002116", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Implied / Accumulator addressing
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Nop_ImpliedNoOperandNoSuffix()
    {
        var result  = SnesAsmExporter.Export(MakeStore([Op()]), [0xEA]);
        var nopLine = result.Split('\n')
            .First(l => l.Contains("NOP"))
            .TrimEnd();
        Assert.EndsWith("NOP", nopLine);
    }

    [Fact]
    public void Export_AslAcc_ShowsAccSuffix()
    {
        var result = SnesAsmExporter.Export(MakeStore([Op()]), [0x0A]);
        Assert.Contains("ASL A", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Immediate modes (M and X flags) — with .B/.W suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_LdaImm_MTrue_OneByteOperand()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(m: true), Opr()]), [0xA9, 0x42]);
        Assert.Contains("LDA.B #$42", result);
    }

    [Fact]
    public void Export_LdaImm_MFalse_TwoByteOperand()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(m: false), Opr(), Opr()]), [0xA9, 0x34, 0x12]);
        Assert.Contains("LDA.W #$1234", result);
    }

    [Fact]
    public void Export_LdxImm_XTrue_OneByteOperand()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(x: true), Opr()]), [0xA2, 0xFF]);
        Assert.Contains("LDX.B #$FF", result);
    }

    [Fact]
    public void Export_LdxImm_XFalse_TwoByteOperand()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(x: false), Opr(), Opr()]), [0xA2, 0xCD, 0xAB]);
        Assert.Contains("LDX.W #$ABCD", result);
    }

    [Fact]
    public void Export_Sep_AlwaysOneByteImmediate_NoSuffix()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xE2, 0x30]);
        Assert.Contains("SEP #$30", result);
    }

    [Fact]
    public void Export_Rep_AlwaysOneByteImmediate_NoSuffix()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xC2, 0x30]);
        Assert.Contains("REP #$30", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Branch instructions with label resolution — no suffix for relative modes
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_BranchForward_UsesLabelName()
    {
        // BNE at $008000, operand +2 → target = $008004
        var snesTarget = 0x008004;
        var store = MakeStore(
            [Op(), Opr()],
            labels: new() { [snesTarget] = "BranchTarget" });

        var result = SnesAsmExporter.Export(store, [0xD0, 0x02]);
        Assert.Contains("BNE BranchTarget", result);
    }

    [Fact]
    public void Export_BranchBackward_UsesLabelName()
    {
        // BRA at $008002, operand -4 (0xFC signed) → target = $008000
        var ann = new[] { Op(), Opr(), Op(), Opr() };
        var store = MakeStore(ann,
            labels: new() { [0x008000] = "LoopTop" });

        var result = SnesAsmExporter.Export(store, [0xEA, 0xEA, 0x80, 0xFC]);
        Assert.Contains("BRA LoopTop", result);
    }

    [Fact]
    public void Export_BranchNoLabel_UsesHexSnesAddress()
    {
        // BEQ at $008000, operand 0 → target = $008002 (ROM offset 2, beyond this 2-byte ROM).
        // No synth label can be emitted for an out-of-ROM target, so 4-digit hex fallback is used.
        // Low 16 bits of $008002 = $8002.
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xF0, 0x00]);
        Assert.Contains("BEQ $8002", result);
    }

    [Fact]
    public void Export_BranchNoLabel_InRom_UsesSynthLabel()
    {
        // BNE at $808000, operand +2 → target = $808004 (ROM offset 4, within a 5-byte ROM).
        // No user label → synth label CODE_808004 (upper-bank) emitted positionally.
        // ROM: BNE +2, NOP, NOP, NOP, NOP
        var bytes = new byte[] { 0xD0, 0x02, 0xEA, 0xEA, 0xEA };
        var store = MakeStore([Op(), Opr(), Op(), Op(), Op()]);
        var result = SnesAsmExporter.Export(store, bytes);
        Assert.Contains("BNE CODE_808004", result);
        Assert.Contains("CODE_808004:", result);
    }

    [Fact]
    public void Export_BranchToUnreachedByte_EmitsSynthLabelAndDb()
    {
        // BNE at $808000, operand 0x00 → target = $808002 (ROM offset 2, Unreached).
        // Unreached bytes are now emitted as db, so CODE_808002 is a positional label.
        var bytes = new byte[] { 0xD0, 0x00, 0xFF };
        var store = MakeStore([Op(), Opr(), new() { Type = ByteType.Unreached }]);
        var result = SnesAsmExporter.Export(store, bytes);
        Assert.Contains("BNE CODE_808002", result);
        Assert.Contains("CODE_808002:", result);
        Assert.Contains("db $FF", result);
    }

    [Fact]
    public void Export_BranchToOperandByte_UsesLooseLabel()
    {
        // BNE at $808000, operand 0x01 → target = $808003 (ROM offset 3, Operand of LDA).
        // Layout: BNE +1 | BNE-opr | LDA.B opcode | LDA.B opr ← branch target | NOP
        //   offset 0 = BNE (Op), 1 = BNE opr (Opr)
        //   offset 2 = LDA.B opcode (Op), 3 = LDA.B operand (Opr) ← branch lands here
        //   offset 4 = NOP (Op)
        // Target is an Operand byte inside LDA — generates LOOSE_OP_008003 = $008003.
        var bytes = new byte[] { 0xD0, 0x01, 0xA5, 0x00, 0xEA };
        var store = MakeStore([Op(), Opr(), Op(), Opr(), Op()]);
        var result = SnesAsmExporter.Export(store, bytes);
        Assert.Contains("BNE LOOSE_OP_008003", result);
        Assert.Contains("LOOSE_OP_008003 = $008003", result);
        Assert.DoesNotContain("CODE_008003", result);
        Assert.DoesNotContain("BNE $8003", result);
    }

    [Fact]
    public void Export_Per_NeverGeneratesSynthLabel_UsesRawOffset()
    {
        // PER ($62) operand is a literal 16-bit relative offset encoded as-is by asar.
        // PER at $808000, 16-bit operand bytes 0x73 0x01 → raw offset = $0173.
        // No synth or loose label; output is "PER $0173" so asar encodes 62 73 01.
        // Layout: PER (3 bytes), NOP
        var bytes = new byte[] { 0x62, 0x73, 0x01, 0xEA };
        var store = MakeStore([Op(), Opr(), Opr(), Op()]);
        var result = SnesAsmExporter.Export(store, bytes);
        Assert.DoesNotContain("CODE_008176", result);
        Assert.DoesNotContain("CODE_808176", result);
        Assert.DoesNotContain("LOOSE_OP_008176", result);
        Assert.Contains("PER $0173", result);
        Assert.DoesNotContain("PER $8176", result);
    }

    [Fact]
    public void Export_Brl_LongBranchResolvesLabel()
    {
        // BRL at $008000, signed 16-bit operand 0x0005 → target = $008008
        var store = MakeStore(
            [Op(), Opr(), Opr()],
            labels: new() { [0x008008] = "FarTarget" });
        var result = SnesAsmExporter.Export(store, [0x82, 0x05, 0x00]);
        Assert.Contains("BRL FarTarget", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // JSR / JMP absolute with label — .W suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_JsrAbs_UsesLabelName()
    {
        // JSR $C000 at $008000 → code-bank target = $00C000
        var store = MakeStore(
            [Op(), Opr(), Opr()],
            labels: new() { [0x00C000] = "Subroutine" });
        var result = SnesAsmExporter.Export(store, [0x20, 0x00, 0xC0]);
        Assert.Contains("JSR.W Subroutine", result);
    }

    [Fact]
    public void Export_JmpAbs_UsesLabelName()
    {
        var store = MakeStore(
            [Op(), Opr(), Opr()],
            labels: new() { [0x008010] = "JumpDest" });
        var result = SnesAsmExporter.Export(store, [0x4C, 0x10, 0x80]);
        Assert.Contains("JMP.W JumpDest", result);
    }

    [Fact]
    public void Export_JmpAbsNoLabel_UsesHex()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0x4C, 0x00, 0xC0]);
        Assert.Contains("JMP.W $C000", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // JSL / JML (24-bit absolute long) with label — .L suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_JslAbsL_UsesLabelName()
    {
        // JSL $018000
        var store = MakeStore(
            [Op(), Opr(), Opr(), Opr()],
            labels: new() { [0x018000] = "Bank1Entry" });
        var result = SnesAsmExporter.Export(store, [0x22, 0x00, 0x80, 0x01]);
        Assert.Contains("JSL.L Bank1Entry", result);
    }

    [Fact]
    public void Export_JmlAbsL_HexWhenNoLabel()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr(), Opr()]), [0x5C, 0x00, 0x80, 0x02]);
        Assert.Contains("JML.L $028000", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Absolute addressing — .W suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_LdaAbs_FormatsWithFourHexDigits()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0xAD, 0x00, 0x02]);
        Assert.Contains("LDA.W $0200", result);
    }

    [Fact]
    public void Export_StaAbsX_FormatsWithXSuffix()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0x9D, 0x00, 0x20]);
        Assert.Contains("STA.W $2000,X", result);
    }

    [Fact]
    public void Export_LdaAbsLong_FormatsSixHexDigits()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr(), Opr()]), [0xAF, 0x56, 0x34, 0x12]);
        Assert.Contains("LDA.L $123456", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Direct page addressing — .B suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_LdaDp_FormatsWithTwoHexDigits()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xA5, 0x42]);
        Assert.Contains("LDA.B $42", result);
    }

    [Fact]
    public void Export_StaDpX_FormatsWithXSuffix()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0x95, 0x10]);
        Assert.Contains("STA.B $10,X", result);
    }

    [Fact]
    public void Export_LdaDpIndirect_FormatsWithParens()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xB2, 0x20]);
        Assert.Contains("LDA.B ($20)", result);
    }

    [Fact]
    public void Export_LdaDpIndirectLong_FormatsWithBrackets()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xA7, 0x30]);
        Assert.Contains("LDA.B [$30]", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Stack-relative addressing — .B suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_LdaSr_FormatsWithSSuffix()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xA3, 0x03]);
        Assert.Contains("LDA.B $03,S", result);
    }

    [Fact]
    public void Export_LdaSrIndY_FormatsCorrectly()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xB3, 0x05]);
        Assert.Contains("LDA.B ($05,S),Y", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Indirect absolute addressing — .W suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_JmpAbsInd_FormatsWithParens()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0x6C, 0x34, 0x12]);
        Assert.Contains("JMP.W ($1234)", result);
    }

    [Fact]
    public void Export_JsrAbsIndX_FormatsWithParensAndX()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0xFC, 0x00, 0x90]);
        Assert.Contains("JSR.W ($9000,X)", result);
    }

    [Fact]
    public void Export_JmlAbsIndL_FormatsWithBrackets()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0xDC, 0x00, 0xFF]);
        Assert.Contains("JML.W [$FF00]", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Block move (MVN / MVP) — no suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Mvn_OutputsBothBankBytes()
    {
        // MVN opcode $54, operand bytes $7E $00
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0x54, 0x7E, 0x00]);
        Assert.Contains("MVN $7E,$00", result);
    }

    [Fact]
    public void Export_Mvp_OutputsBothBankBytes()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0x44, 0x01, 0x02]);
        Assert.Contains("MVP $01,$02", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PEA (push effective absolute) — .W suffix, no #
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Pea_FormatsWithoutHash()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr(), Opr()]), [0xF4, 0x34, 0x12]);
        Assert.Contains("PEA.W $1234", result);
        Assert.DoesNotContain("PEA #", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PEI (push effective indirect) — .B suffix
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Pei_FormatsWithParens()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0xD4, 0x10]);
        Assert.Contains("PEI.B ($10)", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WDM — no suffix (Imm8)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Wdm_OneByteImmediate()
    {
        var result = SnesAsmExporter.Export(
            MakeStore([Op(), Opr()]), [0x42, 0xAB]);
        Assert.Contains("WDM #$AB", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Data directives
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Data8_EmitsDb()
    {
        var result = SnesAsmExporter.Export(MakeStore([D8()]), [0xAB]);
        Assert.Contains("db $AB", result);
    }

    [Fact]
    public void Export_Data8_GroupsConsecutive()
    {
        var ann = new[] { D8(), D8(), D8() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0x01, 0x02, 0x03]);
        Assert.Contains("db $01,$02,$03", result);
    }

    [Fact]
    public void Export_Data16_EmitsDw()
    {
        var ann = new[] { D16(), D16() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0x34, 0x12]);
        Assert.Contains("dw $1234", result);
    }

    [Fact]
    public void Export_Data16_GroupsMultipleWords()
    {
        var ann = new[] { D16(), D16(), D16(), D16() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0x01, 0x00, 0x02, 0x00]);
        // Should emit two dw elements on one line (or split at 16 bytes — only 4 bytes here)
        Assert.Contains("dw $0001,$0002", result);
    }

    [Fact]
    public void Export_Data24_EmitsDl()
    {
        var ann = new[] { D24(), D24(), D24() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0x56, 0x34, 0x12]);
        Assert.Contains("dl $123456", result);
    }

    [Fact]
    public void Export_Data32_EmitsDd()
    {
        var ann = new[] { D32(), D32(), D32(), D32() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0x78, 0x56, 0x34, 0x12]);
        Assert.Contains("dd $12345678", result);
    }

    [Fact]
    public void Export_Data8_LineBreaksAt16Bytes()
    {
        // 17 db bytes → 2 lines (16 on first, 1 on second).
        var ann = Enumerable.Repeat(D8(), 17).ToArray();
        var rom = Enumerable.Range(0, 17).Select(i => (byte)i).ToArray();
        var result = SnesAsmExporter.Export(MakeStore(ann), rom);

        var dbLines = result.Split('\n').Where(l => l.TrimStart().StartsWith("db ")).ToList();
        Assert.Equal(2, dbLines.Count);
    }

    [Fact]
    public void Export_Data_BreaksGroupAtLabel()
    {
        // First db byte at $008000, second db byte at $008001 with a label.
        // Should produce two separate db lines.
        int addr1 = LoRomBase + 1;
        var store = MakeStore(
            [D8(), D8()],
            labels: new() { [addr1] = "SecondByte" });

        var result  = SnesAsmExporter.Export(store, [0xAA, 0xBB]);
        var dbLines = result.Split('\n')
            .Where(l => l.TrimStart().StartsWith("db "))
            .ToList();

        Assert.Equal(2, dbLines.Count);
    }

    [Fact]
    public void Export_Data_BreaksGroupAtInlineComment()
    {
        int addr1 = LoRomBase + 1;
        var store = MakeStore(
            [D8(), D8()],
            comments: new() { [addr1] = "second byte" });

        var result  = SnesAsmExporter.Export(store, [0x01, 0x02]);
        var dbLines = result.Split('\n')
            .Where(l => l.TrimStart().StartsWith("db "))
            .ToList();

        Assert.Equal(2, dbLines.Count);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LoRom bank boundary — data must not cross $xxFFFF → $yy8000 gap
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_Data_DoesNotCrossLoRomBankBoundary()
    {
        // LoRom bank 0: offsets 0x0000-0x7FFF → SNES $808000-$80FFFF
        // LoRom bank 1: offsets 0x8000-0xFFFF → SNES $818000-$81FFFF
        // Place 4 data bytes: last 2 of bank 0 and first 2 of bank 1.
        int bankSize = 0x8000;
        var ann = new ByteAnnotation[bankSize + 2];
        for (int idx = 0; idx < ann.Length; idx++)
            ann[idx] = new ByteAnnotation { Type = ByteType.Unreached };
        ann[bankSize - 2] = D8();
        ann[bankSize - 1] = D8();
        ann[bankSize]     = D8();
        ann[bankSize + 1] = D8();

        var rom = new byte[bankSize + 2];
        rom[bankSize - 2] = 0xAA;
        rom[bankSize - 1] = 0xBB;
        rom[bankSize]     = 0xCC;
        rom[bankSize + 1] = 0xDD;

        var result = SnesAsmExporter.Export(MakeStore(ann), rom);

        // Data must not cross the bank boundary ($AA,$BB in bank 0, $CC,$DD in bank 1).
        Assert.Contains("db $AA,$BB", result);
        Assert.Contains("db $CC,$DD", result);
        // Each bank must get its own org directive (upper-bank form for LoRom).
        Assert.Contains("org $808000", result);
        Assert.Contains("org $818000", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Type mixing: different data widths don't merge
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_DifferentDataTypes_NotGrouped()
    {
        // db followed by dw — must be separate lines.
        var ann = new[] { D8(), D16(), D16() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0x01, 0x34, 0x12]);

        Assert.Contains("db $01", result);
        Assert.Contains("dw $1234", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Mixed code and data
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_CodeThenData_BothPresent()
    {
        var ann = new[] { Op(), D8(), D8() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0xEA, 0x01, 0x02]);
        Assert.Contains("NOP",         result);
        Assert.Contains("db $01,$02",  result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Instruction size / operand count correctness
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_TwoInstructions_BothDisassembled()
    {
        var ann = new[] { Op(), Op() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0xEA, 0x18]); // NOP, CLC
        Assert.Contains("NOP", result);
        Assert.Contains("CLC", result);
    }

    [Fact]
    public void Export_InstructionFollowedByData_NoOrgInBetween()
    {
        // NOP (1 byte) at $808000, db at $808001 — contiguous, no extra org.
        var ann = new[] { Op(), D8() };
        var result = SnesAsmExporter.Export(MakeStore(ann), [0xEA, 0xFF]);

        var orgLines = result.Split('\n').Where(l => l.Contains("org")).ToList();
        // Only the initial org $808000 should appear.
        Assert.Single(orgLines);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HiRom address mapping — $C00000 convention
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_HiRom_FirstByteOrg()
    {
        // HiRom offset 0 → canonical SNES $400000.
        var result = SnesAsmExporter.Export(
            MakeStore([Op()], map: RomMapMode.HiRom), [0xEA]);
        Assert.Contains("org $400000", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Instruction table completeness (spot checks)
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00 },          "BRK")]
    [InlineData(new byte[] { 0x18 },                "CLC")]
    [InlineData(new byte[] { 0x38 },                "SEC")]
    [InlineData(new byte[] { 0x58 },                "CLI")]
    [InlineData(new byte[] { 0x78 },                "SEI")]
    [InlineData(new byte[] { 0xD8 },                "CLD")]
    [InlineData(new byte[] { 0xF8 },                "SED")]
    [InlineData(new byte[] { 0xB8 },                "CLV")]
    [InlineData(new byte[] { 0xEA },                "NOP")]
    [InlineData(new byte[] { 0x40 },                "RTI")]
    [InlineData(new byte[] { 0x60 },                "RTS")]
    [InlineData(new byte[] { 0x6B },                "RTL")]
    [InlineData(new byte[] { 0x48 },                "PHA")]
    [InlineData(new byte[] { 0x68 },                "PLA")]
    [InlineData(new byte[] { 0xFB },                "XCE")]
    [InlineData(new byte[] { 0xDB },                "STP")]
    [InlineData(new byte[] { 0xCB },                "WAI")]
    [InlineData(new byte[] { 0xEB },                "XBA")]
    public void Export_Opcode_ContainsMnemonic(byte[] rom, string mnemonic)
    {
        var ann = rom.Select(_ => (ByteAnnotation)default with { Type = ByteType.Opcode })
                     .Append(ByteAnnotation.Default with { Type = ByteType.Operand })
                     .Append(ByteAnnotation.Default with { Type = ByteType.Operand })
                     .Take(rom.Length)
                     .ToArray();

        // Pad rom to length of ann.
        var paddedRom = rom.Concat(Enumerable.Repeat((byte)0, ann.Length - rom.Length)).ToArray();

        var result = SnesAsmExporter.Export(MakeStore(ann), paddedRom);
        Assert.Contains(mnemonic, result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Label assignment fallback for labels at unvisited offsets
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_UserLabelAtOperandByte_EmittedAsAssignment()
    {
        // JSL abs.long (opcode $22, 3 operand bytes) at offset 0 → emits size 4.
        // Offset 2 is an operand byte; user has placed a label there.
        // The main loop never visits offset 2, so the label must become an assignment.
        // JSL $808080 → bytes: 22 80 80 80
        var bytes = new byte[] { 0x22, 0x80, 0x80, 0x80, 0xEA };
        var store = MakeStore(
            [Op(), Opr(), Opr(), Opr(), Op()],
            labels: new() { [0x808082] = "myTable" });  // offset 2 = SNES $808082

        var result = SnesAsmExporter.Export(store, bytes);

        // Should NOT be a positional label (offset 2 is never the loop entry point).
        Assert.DoesNotContain("myTable:", result);
        // Should appear as an address assignment.
        Assert.Contains("myTable = $", result);
    }

    [Fact]
    public void Export_SynthLabelAtSkippedOffset_EmittedAsAssignment()
    {
        // Simulates the BuildStoreFromMesen case: all code bytes are Opcode,
        // so an operand byte of JSL gets a synth CODE_ label from a BNE branch,
        // but the main loop skips it.
        //
        // Layout (LoRom, all bytes Opcode):
        //   offset 0: BNE rel8 → target offset 4 (SNES $808004)
        //   offset 1: rel8 operand (value 0x02 → target = $808004)
        //   offset 2: JSL abs.long (opcode $22, 3-byte operand) — fills offsets 3,4,5
        //   offset 3: JSL opr byte 0
        //   offset 4: JSL opr byte 1  ← BNE target; synth label CODE_808004 generated
        //   offset 5: JSL opr byte 2
        //   offset 6: NOP
        //
        // With all bytes as Opcode, the loop processes 0 (BNE, size 2), then 2 (JSL, size 4),
        // then 6 (NOP, size 1). Offset 4 is never visited → CODE_808004 must be assignment.
        var bytes = new byte[] { 0xD0, 0x02, 0x22, 0x00, 0x00, 0x80, 0xEA };
        var store = MakeStore([Op(), Op(), Op(), Op(), Op(), Op(), Op()]);

        var result = SnesAsmExporter.Export(store, bytes);

        Assert.DoesNotContain("CODE_808004:", result);
        Assert.Contains("CODE_808004 = $808004", result);
        Assert.Contains("BNE CODE_808004", result);
    }
}
