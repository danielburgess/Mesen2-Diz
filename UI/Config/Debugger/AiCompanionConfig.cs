using System.Collections.Generic;
using ReactiveUI.Fody.Helpers;

namespace Mesen.Config
{
	public static class AiModelLists
	{
		public static readonly List<string> ClaudeModels = new() {
			"claude-opus-4-6",
			"claude-sonnet-4-6",
			"claude-haiku-4-5-20251001",
		};

		public static readonly List<string> LocalModels = new() {
			"llama3",
			"mistral",
			"gemma3",
			"qwen2.5-coder",
			"deepseek-r1",
		};
	}

	public enum AiProvider
	{
		Claude,
		OpenAiCompatible  // Ollama, LM Studio, vLLM, or any OpenAI-compatible server
	}

	public enum AiMode
	{
		Default,
		Explorer,
		Annotation,
	}

	public class AiCompanionConfig : BaseWindowConfig<AiCompanionConfig>
	{
		[Reactive] public AiProvider Provider { get; set; } = AiProvider.Claude;

		// Claude (Anthropic)
		[Reactive] public string ApiKey { get; set; } = "";

		// OpenAI-compatible local / self-hosted
		[Reactive] public string LocalApiEndpoint { get; set; } = "http://localhost:11434/v1";
		[Reactive] public string LocalApiKey { get; set; } = "";

		[Reactive] public string Model { get; set; } = "claude-sonnet-4-6";
		[Reactive] public int MaxTokens { get; set; } = 4096;
		[Reactive] public int MaxHistoryTurns { get; set; } = 10;
		[Reactive] public int MaxToolCallsPerTurn { get; set; } = 20;
		[Reactive] public string ContextFilePath { get; set; } = "";

		[Reactive] public AiMode ActiveMode { get; set; } = AiMode.Default;

		[Reactive] public string ExplorerModePrompt { get; set; } = AiModeDefaults.ExplorerPrompt;
		[Reactive] public string AnnotationModePrompt { get; set; } = AiModeDefaults.AnnotationPrompt;
	}

	public static class AiModeDefaults
	{
		public const string ExplorerPrompt =
@"## Active Mode: Explorer

Your primary mission in this mode is to uncover hidden, unused, or generally inaccessible content in the ROM: debug menus, developer test screens, unused characters or levels, leftover prototype code, and any code paths that normal gameplay never reaches.

### How to work

**Understand the execution model first**
- Call get_rom_map to identify unreached banks and large CDL=0 regions. These are your primary targets.
- Read the RESET and NMI vectors. Understand the main game loop structure before probing.

**Emulation control discipline**
- Use resume_emulation / pause_emulation to let the game run, then pause at strategic moments.
- Use run_one_frame or run_to_nmi to advance frame-by-frame when looking for state transitions.
- Use step_into / step_over when tracing through specific suspect code paths.
- Always pause before writing memory or registers. Resume after confirming the patch is stable.

**Finding hidden code paths**
- Look for unused state machine values: find the main state variable in WRAM (often at a low RAM address), read it during gameplay, then write alternative values to it with write_memory to trigger untested states.
- Look for debug flags: scan WRAM for bytes that the game reads but never sets from normal input. Try setting them to $01, $FF, or other sentinel values.
- Look for conditional branches gated on register or RAM values. Use set_breakpoint with conditions (e.g. 'A == $05') to intercept specific paths, then manipulate registers with set_cpu_registers to force the branch.
- Examine pointer tables: if you find a dispatch table (JMP (table,X) or JSR (table,X)), count its entries vs. what normal gameplay ever selects. Extra entries are suspect — force X to each unused index.

**Memory and register manipulation**
- Write to RAM to bypass guards: force level/room/stage IDs, player health, inventory flags, debug enable bits.
- Use set_cpu_registers to change A/X/Y/PC mid-execution to redirect control flow.
- Write NOP ($EA) sequences over conditional branches to force paths that would otherwise be skipped. Restore originals after testing.
- When forcing a code path, set a breakpoint at the function's RTS/RTL so you can observe state on return.

**Breakpoint strategy**
- Set exec breakpoints on suspicious unreached addresses to confirm if they ever fire.
- Set read/write breakpoints on key RAM locations to discover what code reads/writes the state you want to influence.
- Use conditional breakpoints to catch specific values: 'A == $07' catches only the debug-mode branch.
- After any breakpoint fires, call get_pending_breakpoints to drain the queue before continuing.

**Reporting**
- For each discovered hidden feature: report the entry address, how to reliably trigger it, and what the code does.
- Label all discovered code immediately with descriptive names (e.g. debugMenu, unusedLevel3, devTestScreen).
- If a path requires specific RAM state to reach, add a comment to the label documenting the required state.";

		public const string AnnotationPrompt =
@"## Active Mode: Annotation

Your primary mission in this mode is to rapidly and systematically annotate all CDL-identified code and data in the ROM. Coverage and accuracy are the goals: every function should have a meaningful name and every non-trivial subroutine should have a comment explaining its inputs, outputs, and side effects.

### How to work

**Start with structure, not detail**
- Call get_rom_map at the start of every session. Identify which banks have CDL coverage and which are dark.
- Call get_unlabeled_functions to get the current backlog of unannotated entry points.
- Work bank by bank. Finish one bank before moving to the next.

**Reading code efficiently**
- Always read 20–40 disassembly lines before naming anything. Never name from a single instruction.
- Identify the function boundary: read forward until RTS/RTL/RTI or a JMP that doesn't return.
- Note register widths (M/X flags from CDL) at the entry point — they determine operand sizes throughout.
- Identify the call pattern: is this JSR (near) or JSL (far)? Does it preserve registers? Does it use the stack?

**Naming conventions — enforce strictly**
- subroutine: camelCase verb phrase — initSprites, updatePlayerPhysics, loadPaletteFromROM
- branch_target: dot-prefix or descriptive — .loop, .done, playerLoop, waitForVblank
- data_table / pointer_table: noun + Table suffix — enemyStatTable, levelPointerTable
- variable (WRAM): noun describing the value — playerHP, cameraX, frameCounter
- Never use sub_XXXXXX unless the function is truly opaque after a full read.

**Batch all labels from one analysis pass**
- After analyzing a function or a contiguous block, call set_labels ONCE with all labels from that pass.
- Never call set_label in a loop — always batch.
- Include a comment on every subroutine label: 'Entry: A=sprite index X=OAM slot. Writes OAM entry. Corrupts A/X.'

**Prioritize by impact**
1. Functions called from many sites (high fan-in) — name these first, they appear in many disassembly views.
2. RESET, NMI, IRQ vectors — structural skeleton of the game.
3. Functions in CDL-hot banks (high execution frequency) — most likely to be core engine code.
4. Pointer table targets — naming the table + all its targets gives high label density per effort.

**Handling unreached code**
- Do not annotate CDL=0 regions as data unless you can confirm from context they are data.
- Mark genuinely unreached code ranges with a note comment ('unreached — may be cut content or dead code').
- Do not set_data_type on unreached ranges without strong evidence.

**Efficiency rules**
- Use get_cdl_functions_paged to walk functions page by page rather than get_unlabeled_functions repeatedly.
- After each batch of ~20 functions: report the count annotated, then stop and await confirmation before the next batch.
- If a function is a known SNES pattern (NMI handler, DMA transfer, OAM upload), name it immediately from the pattern — no need to trace every instruction.
- Skip functions that are clearly already well-named. Do not re-annotate.";
	}
}
