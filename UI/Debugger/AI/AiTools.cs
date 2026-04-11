using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;
using Mesen.Config;
using Mesen.Debugger;
using Mesen.Debugger.Labels;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Utilities;

namespace Mesen.Debugger.AI
{
	/// <summary>
	/// Tool definitions (JSON schemas) and implementations for the AI companion.
	/// All tools operate on SNES CPU addresses ($BBAAAA) externally and convert
	/// to ROM offsets internally where needed.
	/// </summary>
	public class AiTools
	{
		private readonly CpuType _cpu = CpuType.Snes;
		private readonly MemoryType _romMemType = MemoryType.SnesPrgRom;
		private readonly MemoryType _cpuMemType = MemoryType.SnesMemory;

		// Cache for read-only ROM data (never changes within a session)
		private readonly Dictionary<string, string> _readCache = new();
		// Tracks disassembly addresses already fetched this session
		private readonly HashSet<uint> _fetchedDisasmAddresses = new();

		/// <summary>Called after each tool execution with a human-readable summary line.</summary>
		public Action<string>? OnToolLog { get; set; }

		/// <summary>
		/// Called by get_pending_breakpoints to drain the queue.
		/// Injected from AiCompanionViewModel so the tool can access pending break events.
		/// </summary>
		public Func<List<string>>? GetAndClearPendingBreaks { get; set; }

		/// <summary>Clear caches on ROM load so stale data is never returned.</summary>
		public void Reset()
		{
			_readCache.Clear();
			_fetchedDisasmAddresses.Clear();
		}

		// ── Schema helpers ────────────────────────────────────────────────────

		private static JsonObject Prop(string type, string description)
			=> new JsonObject { ["type"] = type, ["description"] = description };

		private static JsonObject Schema(string description, Dictionary<string, JsonObject> props, string[] required)
		{
			var properties = new JsonObject();
			foreach(var kv in props)
				properties[kv.Key] = kv.Value;
			return new JsonObject {
				["type"] = "object",
				["description"] = description,
				["properties"] = properties,
				["required"] = new JsonArray(required.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
			};
		}

		private static JsonObject Tool(string name, string description, JsonObject inputSchema)
			=> new JsonObject {
				["name"] = name,
				["description"] = description,
				["input_schema"] = inputSchema
			};

		// ── Tool list ─────────────────────────────────────────────────────────

		public List<JsonObject> GetDefinitions() => new List<JsonObject> {
			Tool("get_disassembly",
				"Get disassembly at a SNES CPU address. Returns formatted assembly lines with addresses, byte codes, mnemonics, and any existing labels/comments.",
				Schema("", new() {
					["address"] = Prop("string", "SNES CPU address as hex string, e.g. \"$80A300\" or \"80A300\""),
					["line_count"] = Prop("integer", "Number of lines to disassemble (1–256, default 32)")
				}, new[] { "address" })),

			Tool("read_memory",
				"Read bytes from emulated memory. Useful for inspecting data tables, pointers, strings, etc.",
				Schema("", new() {
					["address"] = Prop("string", "Start address as hex string (SNES CPU address)"),
					["length"] = Prop("integer", "Number of bytes to read (1–4096)"),
					["memory_type"] = Prop("string", "One of: cpu, prg_rom, work_ram, save_ram, vram, oam, cgram (default: cpu)")
				}, new[] { "address", "length" })),

			Tool("get_cdl_data",
				"Get Code/Data Logger flags for a ROM address range. Flags per byte: Code=1, Data=2, JumpTarget=4, SubEntryPoint=8, IndexMode8=16, MemoryMode8=32.",
				Schema("", new() {
					["rom_offset"] = Prop("integer", "ROM file offset (0-based integer, not SNES address)"),
					["length"] = Prop("integer", "Number of bytes (1–4096)")
				}, new[] { "rom_offset", "length" })),

			Tool("get_labels",
				"Get current labels. Optionally filter by name substring, prefix, category, and/or SNES CPU address range. All filters are combined (AND). Returns address, memory type, name, comment, and category for each match.",
				Schema("", new() {
					["filter"]      = Prop("string", "Optional substring to match against label names (case-insensitive)."),
					["prefix"]      = Prop("string", "Optional prefix to match against label names (case-insensitive)."),
					["addr_from"]   = Prop("string", "Optional start of SNES CPU address range (hex, inclusive)."),
					["addr_to"]     = Prop("string", "Optional end of SNES CPU address range (hex, inclusive)."),
					["category"]    = Prop("string", "Optional category name to filter by (e.g. \"Player\", \"Collision\"). Case-insensitive.")
				}, new string[0])),

			Tool("get_label_at",
				"Get the label, comment, and category at a specific SNES CPU address, if any.",
				Schema("", new() {
					["address"] = Prop("string", "SNES CPU address as hex string")
				}, new[] { "address" })),

			Tool("set_label",
				"Set a label name, comment, and/or category at a SNES CPU address. Overwrites any existing label at that address.",
				Schema("", new() {
					["address"]  = Prop("string", "SNES CPU address as hex string"),
					["name"]     = Prop("string", "Label name (alphanumeric + underscore + @, max 100 chars). Empty string to keep existing name."),
					["comment"]  = Prop("string", "Comment text. Empty string to clear comment."),
					["category"] = Prop("string", "Optional category (e.g. \"Player\", \"Collision\", \"None\"). Omit to keep existing.")
				}, new[] { "address", "name" })),

			Tool("set_labels",
				"Set multiple labels in one call. Each entry may include \"address\", \"name\", \"comment\", and optional \"category\". " +
				"Prefer this over repeated set_label calls when annotating many addresses at once.",
				new JsonObject {
					["type"] = "object",
					["properties"] = new JsonObject {
						["labels"] = new JsonObject {
							["type"] = "array",
							["description"] = "Array of label entries to set.",
							["items"] = new JsonObject {
								["type"] = "object",
								["properties"] = new JsonObject {
									["address"]  = Prop("string", "SNES CPU address as hex string"),
									["name"]     = Prop("string", "Label name. Empty string to keep existing name."),
									["comment"]  = Prop("string", "Comment text. Empty string to clear comment."),
									["category"] = Prop("string", "Optional category name (e.g. \"Player\", \"None\").")
								},
								["required"] = new JsonArray("address", "name")
							}
						}
					},
					["required"] = new JsonArray("labels")
				}),

			Tool("get_functions_by_category",
				"Get all labeled functions that belong to a specific category.",
				Schema("", new() {
					["category"] = Prop("string", "Category name (e.g. \"Collision\", \"Player\", \"AI\"). Case-insensitive.")
				}, new[] { "category" })),

			Tool("delete_label",
				"Delete the label at a SNES CPU address.",
				Schema("", new() {
					["address"] = Prop("string", "SNES CPU address as hex string")
				}, new[] { "address" })),

			Tool("delete_labels",
				"Delete multiple labels in one call. Prefer this over repeated delete_label calls.",
				new JsonObject {
					["type"] = "object",
					["properties"] = new JsonObject {
						["addresses"] = new JsonObject {
							["type"] = "array",
							["description"] = "Array of SNES CPU addresses (hex strings) whose labels should be deleted.",
							["items"] = Prop("string", "SNES CPU address as hex string")
						}
					},
					["required"] = new JsonArray("addresses")
				}),

			Tool("get_call_stack",
				"Get the current CPU call stack showing active subroutine chain.",
				new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

			Tool("get_annotation_summary",
				"Get high-level statistics: total ROM bytes, CDL-covered code bytes, labeled functions, unannotated jump targets, etc.",
				new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("get_rom_map",
			"Get a full ROM overview in one call: all labels (with approximate sizes and comments), CDL coverage stats, and contiguous unreached (CDL=0) address ranges. " +
			"Use this instead of calling get_labels + get_annotation_summary + get_cdl_data separately when you want a broad picture of what is annotated and what still needs work. " +
			"Optionally restrict output to a single SNES bank.",
			Schema("", new() {
				["bank"]              = Prop("string",  "Optional: restrict output to one SNES bank, e.g. \"80\" or \"$80\". Omit for the whole ROM."),
				["include_unreached"] = Prop("boolean", "Include contiguous unreached (CDL=0) address ranges (default true)."),
				["max_ranges"]        = Prop("integer", "Maximum unreached ranges to list (1\u2013500, default 100).")
			}, Array.Empty<string>())),

		// ── Emulation control ──────────────────────────────────────────────

		Tool("pause_emulation",
			"Pause the emulator. Required before reading or modifying CPU registers.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("resume_emulation",
			"Resume emulation from a paused or breakpoint-stopped state.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("reset_game",
			"Reset the currently loaded game.",
			Schema("", new() {
				["type"] = Prop("string", "'soft' = SNES reset button (default); 'hard' = power cycle (full restart)")
			}, Array.Empty<string>())),

		// ── Breakpoints ────────────────────────────────────────────────────

		Tool("list_breakpoints",
			"List all currently active breakpoints.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("set_breakpoint",
			"Add a breakpoint. Emulation pauses when execution/access hits the address (or range) and the optional condition is true.\n" +
			"\nbreak_on modes:\n" +
			"  exec       — pause when the CPU executes the instruction at this address (default)\n" +
			"  read       — pause on any read from this address\n" +
			"  write      — pause on any write to this address\n" +
			"  read_write — pause on read OR write\n" +
			"  all        — pause on exec, read, or write\n" +
			"\nCondition expression syntax (C-like, evaluated each time the breakpoint is hit):\n" +
			"  Registers : A, X, Y, SP, PC, K, DBR, D, PS\n" +
			"  Flags     : N, V, M, IX (X), D, I, Z, C  (each is 0 or 1)\n" +
			"  Memory    : [$7E0010] reads 1 byte from CPU-bus address; [label] reads from a named label\n" +
			"  Operators : == != < > <= >= && || ! + - * / & | ^ ~ << >>\n" +
			"  Literals  : decimal (10), hex ($1A), binary (%00011010)\n" +
			"  Examples  : \"A == $FF\"  \"X > 0 && [$7E0040] != 0\"  \"PS & $20\" (M flag set)\n" +
			"\nFor a range breakpoint supply both address and end_address (e.g. any write to $7E0100–$7E01FF).",
			Schema("", new() {
				["address"]     = Prop("string",  "Start SNES CPU address as hex string ($BBAAAA)"),
				["end_address"] = Prop("string",  "Optional end address for a range breakpoint. If omitted, breakpoint applies to the single address only."),
				["break_on"]    = Prop("string",  "exec (default), read, write, read_write, or all"),
				["memory_type"] = Prop("string",  "cpu (default = CPU bus), work_ram, save_ram, vram, oam, cgram"),
				["condition"]   = Prop("string",  "Optional condition expression (see description). Leave empty to always break.")
			}, new[] { "address" })),

		Tool("remove_breakpoint",
			"Remove all breakpoints at the given address.",
			Schema("", new() {
				["address"]     = Prop("string", "SNES CPU address as hex string"),
				["memory_type"] = Prop("string", "Memory space (default: cpu)")
			}, new[] { "address" })),

		// ── Memory write ───────────────────────────────────────────────────

		Tool("write_memory",
			"Write bytes to emulated memory. Takes effect immediately, even while running.",
			Schema("", new() {
				["address"]     = Prop("string", "Start address as hex string"),
				["data"]        = Prop("string", "Bytes as space-separated hex pairs, e.g. A9 00 85 7E"),
				["memory_type"] = Prop("string", "cpu (default), work_ram, save_ram, vram, oam, cgram, prg_rom")
			}, new[] { "address", "data" })),

		// ── CPU state ──────────────────────────────────────────────────────

		Tool("get_cpu_state",
			"Get all 65816 CPU registers and processor status flags. Pause emulation first for a stable snapshot.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("set_cpu_registers",
			"Set one or more 65816 CPU registers or the full status flags byte. Emulation must be paused. Only provided fields are modified.",
			Schema("", new() {
				["a"]   = Prop("integer", "Accumulator (0-65535)"),
				["x"]   = Prop("integer", "X index register (0-65535)"),
				["y"]   = Prop("integer", "Y index register (0-65535)"),
				["sp"]  = Prop("integer", "Stack pointer (0-65535)"),
				["d"]   = Prop("integer", "Direct page register (0-65535)"),
				["pc"]  = Prop("integer", "Program counter offset within bank K (0-65535)"),
				["k"]   = Prop("integer", "Program bank register (0-255)"),
				["dbr"] = Prop("integer", "Data bank register (0-255)"),
				["ps"]  = Prop("integer", "Processor status byte: bit0=C bit1=Z bit2=I bit3=D bit4=X(idx8) bit5=M(mem8) bit6=V bit7=N")
			}, Array.Empty<string>())),

		// ── CDL function enumeration ───────────────────────────────────────

		Tool("get_unlabeled_functions",
			"Get CDL-identified function entry points (SubEntryPoint-flagged addresses) that are missing a label name, a comment, or both. " +
			"Use this to discover unannotated functions that still need annotation work. Each result includes the SNES address, ROM offset, " +
			"CDL flags, and whatever label/comment currently exists. Also returns the current CPU state at the bottom of the response. " +
			"Preferred over get_annotation_summary when you want an actionable list rather than aggregate counts.",
			Schema("", new() {
				["filter"]      = Prop("string",  "Which entries to include: 'no_label' (name is empty), 'no_comment' (comment is empty), " +
				                                   "'either' (name OR comment empty — default), 'both' (name AND comment both empty)"),
				["bank"]        = Prop("string",  "Optional: restrict to one SNES bank, e.g. \"$80\". Omit to search all banks."),
				["max_results"] = Prop("integer", "Maximum entries to return (1–1000, default 200)")
			}, Array.Empty<string>())),

		Tool("get_cdl_functions_paged",
			"Get a paged list of all CDL-identified functions in a single SNES bank, with optional filtering. Each entry includes SNES address, " +
			"ROM offset, CDL flags, current label name, and comment. Use this to systematically enumerate and work through every function in a bank " +
			"without fetching the entire ROM at once. Iterate pages by incrementing the page parameter. Also returns the current CPU state. " +
			"Use get_unlabeled_functions instead when you only want functions that still need work across all banks.",
			Schema("", new() {
				["bank"]      = Prop("string",  "SNES bank to enumerate (required), e.g. \"$80\" or \"80\""),
				["page"]      = Prop("integer", "Page number, 0-based (default 0)"),
				["page_size"] = Prop("integer", "Functions per page (1–200, default 50)"),
				["filter"]    = Prop("string",  "Filter entries: 'all' (default), 'no_label' (no name), 'no_comment' (no comment), " +
				                                "'unannotated' (no name AND no comment), 'labeled' (has a name)")
			}, new[] { "bank" })),

		// ── Execution stepping ────────────────────────────────────────────

		Tool("step_into",
			"Execute one CPU instruction, following JSR/JSL calls down into subroutines. " +
			"Emulation must be paused. Returns the new CPU state and disassembly at the resulting PC.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("step_over",
			"Execute one CPU instruction, treating JSR/JSL calls as a single step (runs the called routine and returns). " +
			"Emulation must be paused. Returns new CPU state and disassembly at the resulting PC.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("step_out",
			"Run until the current subroutine returns (executes until the matching RTS/RTL/RTI), then pause. " +
			"Emulation must be paused. Returns new CPU state after return.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("step_back",
			"Undo the most recently executed instruction (step backward one instruction). " +
			"Requires the step-back feature to be enabled in debugger settings. Emulation must be paused.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("step_back_scanline",
			"Rewind execution to the start of the previous PPU scanline. " +
			"Requires step-back feature. Emulation must be paused.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("step_back_frame",
			"Rewind execution to the start of the previous video frame. " +
			"Requires step-back feature. Emulation must be paused.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("run_cpu_cycle",
			"Advance execution by exactly one CPU master clock cycle, then pause. " +
			"Much finer-grained than step_into. Emulation must be paused.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("run_ppu_cycle",
			"Advance execution by exactly one PPU dot (pixel clock cycle), then pause. " +
			"Use this to observe PPU state changes at sub-scanline precision. Emulation must be paused.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("run_one_frame",
			"Advance execution by one complete video frame (262 scanlines on NTSC SNES), then pause. " +
			"Useful for observing per-frame state changes. Emulation must be paused.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("run_to_nmi",
			"Resume execution and pause at the next NMI (non-maskable interrupt / V-blank start). " +
			"On SNES, NMI fires once per frame at the start of V-blank (~scanline 225 NTSC). " +
			"Emulation must be paused.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("run_to_irq",
			"Resume execution and pause at the next IRQ (maskable hardware interrupt). " +
			"Emulation must be paused.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("run_to_scanline",
			"Resume execution and pause at the start of a specific PPU scanline. " +
			"SNES NTSC scanline reference: 0–224 = active display, 225 = last visible line, " +
			"225–239 = overscan, 240–261 = V-blank (NMI fires around scanline 225). " +
			"Emulation must be paused.",
			Schema("", new() {
				["scanline"] = Prop("integer", "Target scanline number (0–261 for NTSC SNES)")
			}, new[] { "scanline" })),

		Tool("break_in",
			"Resume execution and pause again after advancing N steps of a specified type. " +
			"Equivalent to the debugger's 'Break In' dialog — useful to skip ahead by a known number of " +
			"instructions or frames without single-stepping through each one. Emulation must be paused.\n" +
			"type values: 'instruction' (default), 'cpu_cycle', 'ppu_cycle', 'ppu_scanline', 'ppu_frame'",
			Schema("", new() {
				["count"] = Prop("integer", "Number of steps to advance before pausing (default 1, min 1)"),
				["type"]  = Prop("string",  "Step unit: 'instruction' (default), 'cpu_cycle', 'ppu_cycle', 'ppu_scanline', 'ppu_frame'")
			}, Array.Empty<string>())),

		// ── Watch expressions ──────────────────────────────────────────────

		Tool("add_watch",
			"Add one or more watch expressions. Watches are evaluated continuously in the debugger's watch panel.\n" +
			"Expression syntax:\n" +
			"  CPU registers : A, X, Y, SP, PC, K, DBR, D, PS\n" +
			"  CPU flags     : N, V, M, IX (X flag), D, I, Z, C  (each 0 or 1)\n" +
			"  Memory read   : [$7E0010] reads 1 byte; [label] reads memory at a named label's address\n" +
			"  Array display : [$300, 16] shows 16 bytes starting at address $300 as space-separated values\n" +
			"  Arithmetic    : A + [$300], X * 2, ([$7E0040] << 8) | [$7E0041]\n" +
			"  Operators     : + - * / & | ^ ~ << >> == != < > <= >= && || !\n" +
			"  Literals      : decimal (255), hex ($FF), binary (%11111111)\n" +
			"Format suffix (append ', FORMAT' to expression):\n" +
			"  ', H'  = hex 1-byte (e.g. 'A, H' → $FF)\n" +
			"  ', H2' = hex 2-byte (e.g. 'A, H2' → $00FF)\n" +
			"  ', S'  = signed decimal\n" +
			"  ', U'  = unsigned decimal\n" +
			"  ', B'  = binary\n" +
			"Examples: 'A, H2'  '[$7E0040], H'  '[$300, 8]'  'X > 0 && [$7E0050] != 0'",
			Schema("", new() {
				["expressions"] = new JsonObject {
					["type"] = "array",
					["description"] = "One or more watch expression strings to add.",
					["items"] = new JsonObject { ["type"] = "string" }
				}
			}, new[] { "expressions" })),

		Tool("get_watches",
			"List all current watch expressions along with their evaluated values. " +
			"Returns the 0-based index, expression string, and current value for each watch. " +
			"The index is used with remove_watch to delete individual watches.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("remove_watch",
			"Remove one or more watch expressions by their 0-based index. " +
			"Use get_watches first to see current entries and their indexes.",
			Schema("", new() {
				["indexes"] = new JsonObject {
					["type"] = "array",
					["description"] = "0-based indexes of watches to remove.",
					["items"] = new JsonObject { ["type"] = "integer" }
				}
			}, new[] { "indexes" })),

		// ── CDL type annotation ────────────────────────────────────────────

		Tool("set_data_type",
			"Explicitly mark an address range in ROM with a CDL type flag: Code, Data, JumpTarget, SubEntryPoint, or None (clears all flags). " +
			"Use this to correct or pre-populate CDL information — for example, mark a known data table as 'data', or declare a routine " +
			"entry point as 'sub_entry' so the disassembler treats it correctly before emulation has covered it. " +
			"The address is a SNES CPU address ($BBAAAA) and the range must map to PRG ROM. " +
			"Note: 'code' and 'data' flags are also set automatically by the CDL as execution/data access occurs; " +
			"'jump_target' and 'sub_entry' are set by branch/JSR targets; use 'none' to clear erroneous flags.",
			Schema("", new() {
				["address"] = Prop("string",  "SNES CPU address of the start of the range (e.g. \"$80A300\")"),
				["length"]  = Prop("integer", "Number of bytes to mark (1–65536)"),
				["type"]    = Prop("string",  "CDL type: 'code', 'data', 'jump_target', 'sub_entry', or 'none' (clears all flags)")
			}, new[] { "address", "length", "type" })),
		Tool("get_pending_breakpoints",
			"Drain the queue of breakpoint events that fired while the AI was busy. Each entry contains the CPU type, " +
			"full CPU register state, and disassembly at the break address — captured at the moment the break occurred. " +
			"Call this after finishing breakpoint analysis to check whether additional breaks hit during processing. " +
			"The queue is cleared once read. Returns an empty result if no breaks are queued.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("list_categories",
			"List all available function categories with their names and one-line descriptions. Use this as a quick reference when deciding which category to assign.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
		};

		// ── Tool executor ─────────────────────────────────────────────────────

		public async Task<string> ExecuteAsync(string toolName, JsonObject input)
		{
			// All DebugApi/LabelManager calls must run on the UI thread
			string result = await Dispatcher.UIThread.InvokeAsync(() => Execute(toolName, input));
			OnToolLog?.Invoke(BuildToolLogEntry(toolName, input, result));
			return result;
		}

		private string Execute(string name, JsonObject input)
		{
			try {
				string result = name switch {
					"get_disassembly"         => DoGetDisassembly(input),
					"read_memory"             => DoReadMemory(input),
					"get_cdl_data"            => DoGetCdlData(input),
					"get_labels"              => DoGetLabels(input),
					"get_label_at"            => DoGetLabelAt(input),
					"set_label"               => DoSetLabel(input),
					"set_labels"              => DoSetLabels(input),
					"get_functions_by_category" => DoGetFunctionsByCategory(input),
					"delete_label"            => DoDeleteLabel(input),
					"delete_labels"           => DoDeleteLabels(input),
					"get_call_stack"          => DoGetCallStack(),
					"get_annotation_summary"  => DoGetAnnotationSummary(),
					"get_rom_map"             => DoGetRomMap(input),
					"pause_emulation"         => DoPauseEmulation(),
					"resume_emulation"        => DoResumeEmulation(),
					"reset_game"              => DoResetGame(input),
					"list_breakpoints"        => DoListBreakpoints(),
					"set_breakpoint"          => DoSetBreakpoint(input),
					"remove_breakpoint"       => DoRemoveBreakpoint(input),
					"write_memory"            => DoWriteMemory(input),
					"get_cpu_state"           => DoGetCpuState(),
					"set_cpu_registers"       => DoSetCpuRegisters(input),
					"get_unlabeled_functions" => DoGetUnlabeledFunctions(input),
					"get_cdl_functions_paged" => DoGetCdlFunctionsPaged(input),
					"set_data_type"           => DoSetDataType(input),
					// Stepping & run control
					"step_into"          => DoStep(StepType.Step, 1),
					"step_over"          => DoStep(StepType.StepOver, 1),
					"step_out"           => DoStep(StepType.StepOut, 1),
					"step_back"          => DoStep(StepType.StepBack, (int)StepBackType.Instruction),
					"step_back_scanline" => DoStep(StepType.StepBack, (int)StepBackType.Scanline),
					"step_back_frame"    => DoStep(StepType.StepBack, (int)StepBackType.Frame),
					"run_cpu_cycle"      => DoStep(StepType.CpuCycleStep, 1),
					"run_ppu_cycle"      => DoStep(StepType.PpuStep, 1),
					"run_one_frame"      => DoStep(StepType.PpuFrame, 1),
					"run_to_nmi"         => DoStep(StepType.RunToNmi, 1),
					"run_to_irq"         => DoStep(StepType.RunToIrq, 1),
					"run_to_scanline"    => DoRunToScanline(input),
					"break_in"           => DoBreakIn(input),
					// Watches
					"add_watch"          => DoAddWatch(input),
					"get_watches"        => DoGetWatches(),
					"remove_watch"       => DoRemoveWatch(input),
					"get_pending_breakpoints" => DoGetPendingBreakpoints(),
				"list_categories"         => DoListCategories(),
					_ => $"Unknown tool: {name}"
				};
				// Append current CPU state + PC disassembly to every response except get_cpu_state
				// (which already IS the full CPU state). This lets the AI see execution context
				// without an extra round-trip tool call.
				return name == "get_cpu_state" ? result : result + GetContextBlock();
			} catch(Exception ex) {
				return $"Tool error: {ex.Message}";
			}
		}

		// ── Implementations ───────────────────────────────────────────────────

		private string DoGetDisassembly(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			int lines = input["line_count"]?.GetValue<int>() ?? 32;
			lines = Math.Clamp(lines, 1, 256);

			bool alreadyFetched = _fetchedDisasmAddresses.Contains(addr);
			_fetchedDisasmAddresses.Add(addr);

			var rows = DebugApi.GetDisassemblyOutput(_cpu, addr, (uint)lines);
			if(rows.Length == 0) return "No disassembly available (is a ROM loaded and is the address valid?)";

			var sb = new StringBuilder();
			if(alreadyFetched)
				sb.AppendLine($"[Note: ${addr:X6} was already read earlier this session — avoid re-analyzing unless labels changed]");
			foreach(var row in rows) {
				string addrStr = row.Address >= 0 ? $"${row.Address:X6}" : "      ";
				string text = row.Text.PadRight(24);
				string comment = row.Comment.Length > 0 ? $"  ; {row.Comment}" : "";
				sb.AppendLine($"{addrStr}  {text}{comment}");
			}
			return sb.ToString();
		}

		private string DoReadMemory(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			int length = Math.Clamp(input["length"]?.GetValue<int>() ?? 16, 1, 4096);
			string memTypeStr = input["memory_type"]?.GetValue<string>() ?? "cpu";

			MemoryType memType = memTypeStr switch {
				"prg_rom" => MemoryType.SnesPrgRom,
				"work_ram" => MemoryType.SnesWorkRam,
				"save_ram" => MemoryType.SnesSaveRam,
				"vram" => MemoryType.SnesVideoRam,
				"oam" => MemoryType.SnesSpriteRam,
				"cgram" => MemoryType.SnesCgRam,
				_ => MemoryType.SnesMemory
			};

			// Only cache reads from static memory regions (ROM/VRAM/OAM/CGRAM never change mid-session)
			bool isStatic = memType == MemoryType.SnesPrgRom || memType == MemoryType.SnesVideoRam
				|| memType == MemoryType.SnesSpriteRam || memType == MemoryType.SnesCgRam;
			string cacheKey = $"mem:{memTypeStr}:{addr}:{length}";
			if(isStatic && _readCache.TryGetValue(cacheKey, out string? cached))
				return "[Cached - returned from earlier read]\n" + cached;

			int memSize = DebugApi.GetMemorySize(memType);
			if(memSize <= 0) return $"Memory type '{memTypeStr}' is not available.";

			uint end = (uint)Math.Min(addr + length - 1, memSize - 1);
			byte[] data = DebugApi.GetMemoryValues(memType, addr, end);

			var sb = new StringBuilder();
			for(int i = 0; i < data.Length; i += 16) {
				sb.Append($"${addr + i:X6}  ");
				int rowLen = Math.Min(16, data.Length - i);
				for(int j = 0; j < rowLen; j++)
					sb.Append($"{data[i + j]:X2} ");
				sb.Append(" ");
				for(int j = 0; j < rowLen; j++)
					sb.Append(data[i + j] >= 0x20 && data[i + j] < 0x7F ? (char)data[i + j] : '.');
				sb.AppendLine();
			}
			string result = sb.ToString();
			if(isStatic) _readCache[cacheKey] = result;
			return result;
		}

		private string DoGetCdlData(JsonObject input)
		{
			int romOffset = input["rom_offset"]?.GetValue<int>() ?? 0;
			int length = Math.Clamp(input["length"]?.GetValue<int>() ?? 64, 1, 4096);

			int romSize = DebugApi.GetMemorySize(_romMemType);
			if(romSize <= 0) return "No ROM loaded.";

			int safeLen = Math.Min(length, romSize - romOffset);
			if(safeLen <= 0) return "Address out of ROM range.";

			var flags = DebugApi.GetCdlData((uint)romOffset, (uint)safeLen, _romMemType);
			var sb = new StringBuilder();
			sb.AppendLine($"ROM offset ${romOffset:X6}, {safeLen} bytes:");
			for(int i = 0; i < flags.Length; i++)
				sb.AppendLine($"  +{i:X4} (offset ${romOffset + i:X6}): {FormatCdlFlags(flags[i])}");
			return sb.ToString();
		}

		private string DoGetLabels(JsonObject input)
		{
			string filter      = input["filter"]?.GetValue<string>()    ?? "";
			string prefix      = input["prefix"]?.GetValue<string>()    ?? "";
			string addrFrom    = input["addr_from"]?.GetValue<string>() ?? "";
			string addrTo      = input["addr_to"]?.GetValue<string>()   ?? "";
			string categoryStr = input["category"]?.GetValue<string>()  ?? "";

			var labels = LabelManager.GetAllLabels();
			if(labels.Count == 0) return "No labels defined.";

			IEnumerable<CodeLabel> filtered = labels;
			if(filter.Length > 0)
				filtered = filtered.Where(l => l.Label.Contains(filter, StringComparison.OrdinalIgnoreCase));
			if(prefix.Length > 0)
				filtered = filtered.Where(l => l.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
			if(addrFrom.Length > 0) {
				uint from = ParseAddress(addrFrom);
				filtered = filtered.Where(l => l.Address >= from);
			}
			if(addrTo.Length > 0) {
				uint to = ParseAddress(addrTo);
				filtered = filtered.Where(l => l.Address <= to);
			}
			if(categoryStr.Length > 0 && Enum.TryParse<FunctionCategory>(categoryStr, ignoreCase: true, out var catFilter))
				filtered = filtered.Where(l => l.Category == catFilter);

			var matches = filtered.OrderBy(l => l.Address).ToList();
			if(matches.Count == 0) return "No labels matched.";

			var sb = new StringBuilder();
			sb.AppendLine($"{matches.Count} label(s):");
			foreach(var lbl in matches) {
				string comment  = lbl.Comment.Length > 0 ? $"  ; {lbl.Comment}" : "";
				string category = lbl.Category != FunctionCategory.None ? $"  [{lbl.Category}]" : "";
				sb.AppendLine($"  {lbl.MemoryType}:${lbl.Address:X6}  {lbl.Label}{category}{comment}");
			}
			return sb.ToString();
		}

		private string DoGetLabelAt(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			var absAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)addr, Type = _cpuMemType });
			var label = LabelManager.GetLabel(absAddr);
			if(label == null)
				return $"No label at ${addr:X6} (ROM offset ${absAddr.Address:X6}).";
			string catLine = label.Category != FunctionCategory.None ? $"\nCategory: {label.Category}" : "";
			return $"Address: ${addr:X6} (ROM ${absAddr.Address:X6})\nName: {label.Label}{catLine}\nComment: {label.Comment}";
		}

		private static FunctionCategory ParseCategory(string? raw, FunctionCategory fallback)
		{
			if(string.IsNullOrEmpty(raw)) return fallback;
			return Enum.TryParse<FunctionCategory>(raw, ignoreCase: true, out var cat) ? cat : fallback;
		}

		private string DoSetLabel(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			string name    = input["name"]?.GetValue<string>()     ?? "";
			string comment = input["comment"]?.GetValue<string>()  ?? "";
			string catStr  = input["category"]?.GetValue<string>() ?? "";

			if(!string.IsNullOrEmpty(name) && !LabelManager.LabelRegex.IsMatch(name))
				return $"Invalid label name '{name}'. Use only letters, digits, underscore, @.";

			var absAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)addr, Type = _cpuMemType });
			if(absAddr.Type == MemoryType.None || absAddr.Address < 0)
				return $"Address ${addr:X6} does not map to ROM.";

			var existing = LabelManager.GetLabel(absAddr);
			if(string.IsNullOrEmpty(name))
				name = existing?.Label ?? "";

			FunctionCategory category = ParseCategory(catStr, existing?.Category ?? FunctionCategory.None);

			LabelManager.SetLabel(new CodeLabel {
				Address    = (uint)absAddr.Address,
				MemoryType = absAddr.Type,
				Label      = name,
				Comment    = comment,
				Length     = 1,
				Category   = category
			}, raiseEvent: true);
			LabelManager.MarkAsAiModified((uint)absAddr.Address, absAddr.Type);
			DebugWorkspaceManager.AutoSave();
			string catPart = category != FunctionCategory.None ? $" category='{category}'" : "";
			return $"Label set: ${addr:X6} → name='{name}'{catPart} comment='{comment}'";
		}

		private string DoSetLabels(JsonObject input)
		{
			var arr = input["labels"]?.AsArray();
			if(arr == null || arr.Count == 0) return "No labels provided.";

			int set = 0, skipped = 0;
			var errors = new List<string>();

			foreach(var node in arr) {
				if(node is not JsonObject entry) continue;
				string addrStr  = entry["address"]?.GetValue<string>()  ?? "";
				string name     = entry["name"]?.GetValue<string>()     ?? "";
				string comment  = entry["comment"]?.GetValue<string>()  ?? "";
				string catStr   = entry["category"]?.GetValue<string>() ?? "";

				if(addrStr.Length == 0) { errors.Add("entry missing address"); continue; }

				if(!string.IsNullOrEmpty(name) && !LabelManager.LabelRegex.IsMatch(name)) {
					errors.Add($"Invalid label name '{name}' for ${addrStr}");
					skipped++;
					continue;
				}

				uint addr = ParseAddress(addrStr);
				var absAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)addr, Type = _cpuMemType });
				if(absAddr.Type == MemoryType.None || absAddr.Address < 0) {
					errors.Add($"${addr:X6} does not map to ROM");
					skipped++;
					continue;
				}

				var existingLabel = LabelManager.GetLabel(absAddr);
				if(string.IsNullOrEmpty(name))
					name = existingLabel?.Label ?? "";

				FunctionCategory category = ParseCategory(catStr, existingLabel?.Category ?? FunctionCategory.None);

				LabelManager.SetLabel(new CodeLabel {
					Address    = (uint)absAddr.Address,
					MemoryType = absAddr.Type,
					Label      = name,
					Comment    = comment,
					Length     = 1,
					Category   = category
				}, raiseEvent: true);
				LabelManager.MarkAsAiModified((uint)absAddr.Address, absAddr.Type);
				set++;
			}

			DebugWorkspaceManager.AutoSave();
			string result = $"Set {set} label(s), skipped {skipped}.";
			if(errors.Count > 0) result += " Errors: " + string.Join("; ", errors);
			return result;
		}

				private string DoDeleteLabel(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			var absAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)addr, Type = _cpuMemType });
			var label = LabelManager.GetLabel(absAddr);
			if(label == null) return $"No label at ${addr:X6}.";
			LabelManager.DeleteLabel(label, raiseEvent: true);
			DebugWorkspaceManager.AutoSave();
			return $"Deleted label '{label.Label}' at ${addr:X6}.";
		}

		private string DoDeleteLabels(JsonObject input)
		{
			var addresses = input["addresses"] as JsonArray;
			if(addresses == null || addresses.Count == 0) return "No addresses provided.";

			var toDelete = new List<CodeLabel>();
			var notFound = new List<string>();

			foreach(var item in addresses) {
				string addrStr = item?.GetValue<string>() ?? "";
				uint addr = ParseAddress(addrStr);
				var absAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)addr, Type = _cpuMemType });
				var label = LabelManager.GetLabel(absAddr);
				if(label == null) {
					notFound.Add($"${addr:X6}");
				} else {
					toDelete.Add(label);
				}
			}

			if(toDelete.Count > 0) {
				LabelManager.DeleteLabels(toDelete);
				DebugWorkspaceManager.AutoSave();
			}

			string result = $"Deleted {toDelete.Count} label(s).";
			if(notFound.Count > 0) result += $" Not found: {string.Join(", ", notFound)}.";
			return result;
		}

		private static string DoListCategories()
		{
			return
@"Available function categories (use exact name in set_label / set_labels / get_functions_by_category):

IMPORTANT: You must always assign a category. None means untouched — never set it yourself.

Init         — One-time hardware/system setup: RESET handler, VRAM/DMA init
MainLoop     — Top-level game loop, frame dispatch
Interrupt    — NMI, IRQ, BRK handlers
DMA          — DMA transfer routines specifically
Input        — Controller reading, button state tracking
Player       — Player movement, state, health, inventory
OAM          — Sprite/OAM buffer management
VRAM         — Tile/graphics uploads to VRAM
Tilemap      — BG tilemap writes and updates
Palette      — Color/palette management
Scrolling    — Scroll register updates, parallax
Animation    — Animation frame sequencing and timers
Effects      — Visual effects: fades, flashes, screen wipes
Mode7        — Mode 7 math, matrix setup
Music        — BGM playback, SPC communication
SFX          — Sound effect triggers
Physics      — Velocity, gravity, movement integration
Collision    — Hit detection and response
Entity       — Generic object/entity lifecycle: spawn, update, despawn
Enemy        — Enemy-specific behavior
AI           — Pathfinding, decision-making, targeting
Camera       — Viewport tracking, scroll bounds
StateMachine — State variable management, mode dispatch tables
GameState    — High-level game mode: title, gameplay, gameover, cutscene
Menu         — Menu navigation and selection logic
HUD          — On-screen display: score, health bars, counters
LevelLoad    — Map/room/level loading and setup
Transition   — Screen transitions, room changes
Script       — Script/event interpreter: bytecode VM, event queue, cutscene sequencing
Dialogue     — Text box lifecycle: window drawing, letter-by-letter print, prompts
Math         — Arithmetic, trig tables, fixed-point math
RNG          — Random number generation
Timer        — Frame counters, countdown timers
Memory       — Memory copy, fill, compression
Text         — Low-level font/string rendering utilities
Save         — SRAM read/write, save data management
Debug        — Developer test code, leftover debug menus
Unused       — Dead code, unreachable, cut content
Unknown      — Analyzed but purpose not yet determined; needs further investigation
Helper       — Small utility/helper that serves other systems; no direct game-system role
None         — RESERVED: means this label has never been touched. Never assign this yourself.";
		}

		private string DoGetFunctionsByCategory(JsonObject input)
		{
			string catStr = input["category"]?.GetValue<string>() ?? "";
			if(!Enum.TryParse<FunctionCategory>(catStr, ignoreCase: true, out var category))
				return $"Unknown category '{catStr}'. Valid values: {string.Join(", ", Enum.GetNames<FunctionCategory>())}";

			var matches = LabelManager.GetAllLabels()
				.Where(l => l.Category == category)
				.OrderBy(l => l.Address)
				.ToList();

			if(matches.Count == 0) return $"No labels with category '{category}'.";

			var sb = new StringBuilder();
			sb.AppendLine($"{matches.Count} label(s) in [{category}]:");
			foreach(var lbl in matches) {
				string comment = lbl.Comment.Length > 0 ? $"  ; {lbl.Comment}" : "";
				sb.AppendLine($"  {lbl.MemoryType}:${lbl.Address:X6}  {lbl.Label}{comment}");
			}
			return sb.ToString();
		}

		private string DoGetCallStack()
		{
			var frames = DebugApi.GetCallstack(_cpu);
			if(frames.Length == 0) return "Call stack is empty (execution not paused or no active calls).";

			var sb = new StringBuilder();
			sb.AppendLine($"Call stack ({frames.Length} frame(s)):");
			for(int i = frames.Length - 1; i >= 0; i--) {
				var f = frames[i];
				string srcLabel = LabelManager.GetLabel(f.AbsSource)?.Label ?? "";
				string tgtLabel = LabelManager.GetLabel(f.AbsTarget)?.Label ?? "";
				sb.AppendLine($"  [{frames.Length - i}] call from ${f.Source:X6}{(srcLabel.Length > 0 ? $" ({srcLabel})" : "")} → ${f.Target:X6}{(tgtLabel.Length > 0 ? $" ({tgtLabel})" : "")}  [ret ${f.Return:X6}]");
			}
			return sb.ToString();
		}

		private string DoGetAnnotationSummary()
		{
			int romSize = DebugApi.GetMemorySize(_romMemType);
			if(romSize <= 0) return "No ROM loaded.";

			var stats = DebugApi.GetCdlStatistics(_romMemType);
			var labels = LabelManager.GetAllLabels();
			int romLabels = labels.Count(l => {
				if(l.MemoryType == _romMemType) return true;
				var abs = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)l.Address, Type = l.MemoryType });
				return abs.Type == _romMemType && abs.Address >= 0;
			});

			// Count functions, jump targets, and unannotated entries in one CDL pass.
			// GetCdlStatistics() leaves FunctionCount/JumpTargetCount == 0 for SNES
			// (the base C++ implementation never fills them), so derive them ourselves.
			var cdl = DebugApi.GetCdlData(0, (uint)romSize, _romMemType);
			int jumpTargets = 0, functions = 0, unannotatedTargets = 0;
			for(int i = 0; i < cdl.Length; i++) {
				bool isJump = (cdl[i] & CdlFlags.JumpTarget) != 0;
				bool isSub  = (cdl[i] & CdlFlags.SubEntryPoint) != 0;
				if(isJump) jumpTargets++;
				if(isSub)  functions++;
				if(isJump || isSub) {
					var abs = new AddressInfo { Address = i, Type = _romMemType };
					if(LabelManager.GetLabel(abs) == null) unannotatedTargets++;
				}
			}

			double codePct = romSize > 0 ? 100.0 * stats.CodeBytes / romSize : 0;
			double dataPct = romSize > 0 ? 100.0 * stats.DataBytes / romSize : 0;

			return $"ROM size: {romSize:N0} bytes\n" +
			       $"CDL coverage: {stats.CodeBytes:N0} code bytes ({codePct:F1}%), {stats.DataBytes:N0} data bytes ({dataPct:F1}%)\n" +
			       $"CDL functions: {functions:N0}  jump targets: {jumpTargets:N0}\n" +
			       $"Labels: {romLabels:N0} on ROM\n" +
			       $"Unannotated CDL targets: {unannotatedTargets:N0}";
		}

		private string BuildToolLogEntry(string name, JsonObject input, string result)
		{
			// Extract meaningful summary for each tool rather than dumping raw result
			string summary = name switch {
				"set_labels" => ExtractSetLabelsSummary(result),
				"set_label"  => ExtractSetLabelSummary(input, result),
				"delete_label"  => $"Deleted label at {input["address"]?.GetValue<string>() ?? "?"}",
				"delete_labels" => $"Deleted {(input["addresses"] as JsonArray)?.Count ?? 0} label(s)",
				"get_functions_by_category" => $"Fetched [{input["category"]?.GetValue<string>() ?? "?"}] functions",
				"set_breakpoint" => $"Set breakpoint at {input["address"]?.GetValue<string>() ?? "?"}" +
				                    (input["break_on"] != null ? $" [{input["break_on"]!.GetValue<string>()}]" : ""),
				"remove_breakpoint" => $"Removed breakpoint at {input["address"]?.GetValue<string>() ?? "?"}",
				"set_data_type" => $"Marked {input["length"]?.GetValue<int>() ?? 0} bytes as '{input["type"]?.GetValue<string>() ?? "?"}' at {input["address"]?.GetValue<string>() ?? "?"}",
				"write_memory" => $"Wrote memory at {input["address"]?.GetValue<string>() ?? "?"}",
				"set_cpu_registers" => $"Set CPU registers: {string.Join(", ", input.Select(kv => $"{kv.Key}={kv.Value}"))}",
				"add_watch" => $"Added {(input["expressions"] as JsonArray)?.Count ?? 0} watch(es)",
				"remove_watch" => $"Removed {(input["indexes"] as JsonArray)?.Count ?? 0} watch(es)",
				"pause_emulation" => "Paused emulation",
				"resume_emulation" => "Resumed emulation",
				"reset_game" => $"Reset game ({input["type"]?.GetValue<string>() ?? "soft"})",
				"step_into" => "Step into",
				"step_over" => "Step over",
				"step_out"  => "Step out",
				"step_back" => "Step back",
				"step_back_scanline" => "Step back scanline",
				"step_back_frame"    => "Step back frame",
				"run_cpu_cycle"      => "Run one CPU cycle",
				"run_ppu_cycle"      => "Run one PPU cycle",
				"run_one_frame"      => "Run one frame",
				"run_to_nmi"         => "Run to NMI",
				"run_to_irq"         => "Run to IRQ",
				"run_to_scanline"    => $"Run to scanline {input["scanline"]?.GetValue<int>() ?? 0}",
				"break_in"           => $"Break in {input["count"]?.GetValue<int>() ?? 1} {input["type"]?.GetValue<string>() ?? "instruction"}(s)",
				"get_disassembly"    => $"Read disassembly at {input["address"]?.GetValue<string>() ?? "?"}",
				"read_memory"        => $"Read memory at {input["address"]?.GetValue<string>() ?? "?"}",
				"get_cdl_data"       => $"Read CDL data at rom offset {input["rom_offset"]?.GetValue<int>() ?? 0}",
				"get_labels"         => "Fetched all labels",
				"get_label_at"       => $"Got label at {input["address"]?.GetValue<string>() ?? "?"}",
				"get_call_stack"     => "Fetched call stack",
				"get_annotation_summary" => "Fetched annotation summary",
				"get_rom_map"        => $"Fetched ROM map{(input["bank"] != null ? $" (bank {input["bank"]!.GetValue<string>()})" : "")}",
				"get_cpu_state"      => "Fetched CPU state",
				"get_unlabeled_functions" => "Fetched unlabeled functions",
				"get_cdl_functions_paged" => $"Fetched CDL functions (bank {input["bank"]?.GetValue<string>() ?? "?"}, page {input["page"]?.GetValue<int>() ?? 0})",
				"list_breakpoints"   => "Listed breakpoints",
				"get_watches"        => "Fetched watches",
				"get_pending_breakpoints" => "Drained pending breakpoint queue",
				"list_categories"         => "Listed function categories",
				_ => name
			};

			string ts = DateTime.Now.ToString("HH:mm:ss");
			return $"[{ts}] {summary}";
		}

		private static string ExtractSetLabelsSummary(string result)
		{
			// result starts with "Set N labels" or "Set N labels, skipped M"
			int set = 0, skipped = 0;
			var setMatch = System.Text.RegularExpressions.Regex.Match(result, @"Set (\d+) label");
			var skipMatch = System.Text.RegularExpressions.Regex.Match(result, @"skipped (\d+)");
			if(setMatch.Success) set = int.Parse(setMatch.Groups[1].Value);
			if(skipMatch.Success) skipped = int.Parse(skipMatch.Groups[1].Value);
			return skipped > 0 ? $"set_labels: Set {set}, skipped {skipped}" : $"set_labels: Set {set}";
		}

		private static string ExtractSetLabelSummary(JsonObject input, string result)
		{
			string addr = input["address"]?.GetValue<string>() ?? "?";
			string name2 = input["name"]?.GetValue<string>() ?? "";
			return string.IsNullOrEmpty(name2)
				? $"set_label: Updated {addr}"
				: $"set_label: {addr} → {name2}";
		}

		private string DoGetRomMap(JsonObject input)
		{
			int romSize = DebugApi.GetMemorySize(_romMemType);
			if(romSize <= 0) return "No ROM loaded.";

			bool includeUnreached = input["include_unreached"]?.GetValue<bool>() ?? true;
			int maxRanges = Math.Clamp(input["max_ranges"]?.GetValue<int>() ?? 100, 1, 500);
			string? bankStr = input["bank"]?.GetValue<string>()?.TrimStart('$');
			int? filterBank = bankStr != null ? Convert.ToInt32(bankStr, 16) : (int?)null;

			var sb = new StringBuilder();

			// CDL stats — FunctionCount/JumpTargetCount are never populated by the SNES
			// CDL implementation (base C++ GetStatistics() leaves them 0), so count them
			// from the raw CDL byte array instead.
			var stats = DebugApi.GetCdlStatistics(_romMemType);
			var cdlFull = DebugApi.GetCdlData(0, (uint)romSize, _romMemType);
			int cdlFunctions = 0, cdlJumpTargets = 0;
			foreach(var b in cdlFull) {
				if((b & CdlFlags.SubEntryPoint) != 0) cdlFunctions++;
				if((b & CdlFlags.JumpTarget) != 0) cdlJumpTargets++;
			}
			double codePct = romSize > 0 ? 100.0 * stats.CodeBytes / romSize : 0;
			double dataPct = romSize > 0 ? 100.0 * stats.DataBytes / romSize : 0;
			sb.AppendLine($"ROM size: {romSize:N0} bytes");
			if(stats.CodeBytes == 0) {
				sb.AppendLine($"CDL coverage: 0 code (0.0%), 0 data (0.0%) — NOTE: CDL is session-transient; 0% means the ROM has not been executed with the debugger active in this session, not that it has never been run. Re-run the game or load a saved CDL file to populate coverage.");
			} else {
				sb.AppendLine($"CDL coverage: {stats.CodeBytes:N0} code ({codePct:F1}%), {stats.DataBytes:N0} data ({dataPct:F1}%), {romSize - stats.CodeBytes - stats.DataBytes:N0} unreached ({100 - codePct - dataPct:F1}%)");
			}
			sb.AppendLine($"CDL functions: {cdlFunctions:N0}  jump targets: {cdlJumpTargets:N0}");

			// Labels — accept any memory type; normalize to ROM offset for dedup/sort.
			// Labels set via the Mesen2 UI use SnesMemory (CPU address space); labels set
			// by the AI tool use SnesPrgRom (ROM offset).  Both are valid and must be shown.
			var seenRomOffsets = new HashSet<int>();
			var entries = new List<(uint romOffset, uint snesAddr, string name, string comment)>();
			foreach(var lbl in LabelManager.GetAllLabels()) {
				// Resolve to absolute ROM address regardless of how the label was stored
				AddressInfo abs = lbl.MemoryType == _romMemType
					? new AddressInfo { Address = (int)lbl.Address, Type = _romMemType }
					: DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)lbl.Address, Type = lbl.MemoryType });
				if(abs.Type != _romMemType || abs.Address < 0) continue;
				if(!seenRomOffsets.Add(abs.Address)) continue;  // deduplicate

				var rel = DebugApi.GetRelativeAddress(abs, _cpu);
				if(rel.Address < 0) continue;
				uint snesAddr = (uint)rel.Address;
				if(filterBank.HasValue && (snesAddr >> 16) != (uint)filterBank.Value) continue;
				entries.Add(((uint)abs.Address, snesAddr, lbl.Label, lbl.Comment));
			}
			entries.Sort((a, b) => a.romOffset.CompareTo(b.romOffset));

			sb.AppendLine();
			sb.AppendLine($"=== Labels ({entries.Count}{(filterBank.HasValue ? $" in bank ${filterBank:X2}" : "")}) ===");
			if(entries.Count == 0) {
				sb.AppendLine("  (none)");
			} else {
				for(int i = 0; i < entries.Count; i++) {
					var (romOff, snesAddr, name, comment) = entries[i];
					string sizeStr = "";
					if(i + 1 < entries.Count) {
						int gap = (int)(entries[i + 1].romOffset - romOff);
						if(gap > 0 && gap < 0x10000) sizeStr = $" (~{gap}b)";
					}
					string commentStr = comment.Length > 0 ? $"  ; {comment}" : "";
					sb.AppendLine($"  ${snesAddr:X6}  {name}{sizeStr}{commentStr}");
				}
			}

			if(!includeUnreached) return sb.ToString();

			// Unreached ranges: CDL == 0
			var cdl = DebugApi.GetCdlData(0, (uint)romSize, _romMemType);
			var ranges = new List<(int start, int end)>();
			int? rangeStart = null;
			for(int i = 0; i <= cdl.Length; i++) {
				bool unreached = i < cdl.Length && cdl[i] == 0;
				if(unreached && rangeStart == null) rangeStart = i;
				else if(!unreached && rangeStart.HasValue) {
					ranges.Add((rangeStart.Value, i - 1));
					rangeStart = null;
				}
			}

			// Filter to bank if requested
			var filteredRanges = new List<(int start, int end)>();
			foreach(var (start, end) in ranges) {
				var rel = DebugApi.GetRelativeAddress(new AddressInfo { Address = start, Type = _romMemType }, _cpu);
				if(rel.Address < 0) continue;
				if(filterBank.HasValue && ((uint)rel.Address >> 16) != (uint)filterBank.Value) continue;
				filteredRanges.Add((start, end));
			}

			int total = filteredRanges.Count;
			sb.AppendLine();
			sb.AppendLine($"=== Unreached Ranges (showing {Math.Min(total, maxRanges)} of {total}) ===");
			int shown = 0;
			foreach(var (start, end) in filteredRanges) {
				if(shown >= maxRanges) break;
				var startRel = DebugApi.GetRelativeAddress(new AddressInfo { Address = start, Type = _romMemType }, _cpu);
				var endRel   = DebugApi.GetRelativeAddress(new AddressInfo { Address = end,   Type = _romMemType }, _cpu);
				string startStr = startRel.Address >= 0 ? $"${startRel.Address:X6}" : $"ROM+{start:X6}";
				string endStr   = endRel.Address   >= 0 ? $"${endRel.Address:X6}"   : $"ROM+{end:X6}";
				sb.AppendLine($"  {startStr}\u2013{endStr}  ({end - start + 1} bytes)");
				shown++;
			}
			return sb.ToString();
		}

		// ── Emulation control implementations ────────────────────────────────

		private static string DoPauseEmulation()
		{
			if(EmuApi.IsPaused()) return "Emulation is already paused.";
			EmuApi.Pause();
			return "Emulation paused.";
		}

		private static string DoResumeEmulation()
		{
			if(!EmuApi.IsPaused()) return "Emulation is already running.";
			DebugApi.ResumeExecution();
			return "Emulation resumed.";
		}

		private static string DoResetGame(JsonObject input)
		{
			string type = input["type"]?.GetValue<string>() ?? "soft";
			if(type == "hard") {
				LoadRomHelper.PowerCycle();
				return "Hard reset (power cycle) triggered.";
			} else {
				LoadRomHelper.Reset();
				return "Soft reset triggered.";
			}
		}

		// ── Breakpoint implementations ────────────────────────────────────────

		private static string DoListBreakpoints()
		{
			var bps = BreakpointManager.Breakpoints;
			if(bps.Count == 0) return "No breakpoints set.";

			var sb = new StringBuilder();
			sb.AppendLine($"{bps.Count} breakpoint(s):");
			foreach(var bp in bps) {
				var types = new List<string>();
				if(bp.BreakOnExec) types.Add("Exec");
				if(bp.BreakOnRead) types.Add("Read");
				if(bp.BreakOnWrite) types.Add("Write");
				string range = bp.StartAddress == bp.EndAddress
					? $"${bp.StartAddress:X6}"
					: $"${bp.StartAddress:X6}–${bp.EndAddress:X6}";
				string cond = bp.Condition.Length > 0 ? $"  cond: {bp.Condition}" : "";
				string en = bp.Enabled ? "" : " [disabled]";
				sb.AppendLine($"  {range}  {bp.MemoryType}  [{string.Join("|", types)}]{cond}{en}");
			}
			return sb.ToString();
		}

		private static string DoSetBreakpoint(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			string? endAddrStr = input["end_address"]?.GetValue<string>();
			uint endAddr = endAddrStr != null ? ParseAddress(endAddrStr) : addr;
			if(endAddr < addr) endAddr = addr;

			string breakOn    = input["break_on"]?.GetValue<string>() ?? "exec";
			string condition  = input["condition"]?.GetValue<string>() ?? "";
			MemoryType memType = ParseMemoryType(input["memory_type"]?.GetValue<string>() ?? "cpu");

			var bp = new Breakpoint {
				StartAddress = addr,
				EndAddress   = endAddr,
				MemoryType   = memType,
				CpuType      = CpuType.Snes,
				Enabled      = true,
				BreakOnExec  = breakOn is "exec" or "all",
				BreakOnRead  = breakOn is "read" or "read_write" or "all",
				BreakOnWrite = breakOn is "write" or "read_write" or "all",
				Condition    = condition
			};

			BreakpointManager.AddBreakpoint(bp);
			BreakpointManager.MarkAsAiSet(bp);
			string range = addr == endAddr ? $"${addr:X6}" : $"${addr:X6}–${endAddr:X6}";
			string cond  = condition.Length > 0 ? $" when ({condition})" : "";
			return $"Breakpoint set: {range} [{memType}, {breakOn}]{cond}.";
		}

		private static string DoRemoveBreakpoint(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			MemoryType memType = ParseMemoryType(input["memory_type"]?.GetValue<string>() ?? "cpu");

			var toRemove = BreakpointManager.Breakpoints
				.Where(bp => bp.StartAddress == addr && bp.MemoryType == memType)
				.ToList();

			if(toRemove.Count == 0)
				return $"No breakpoint found at ${addr:X6} ({memType}).";

			BreakpointManager.RemoveBreakpoints(toRemove);
			return $"Removed {toRemove.Count} breakpoint(s) at ${addr:X6}.";
		}

		// ── Memory write implementation ───────────────────────────────────────

		private static string DoWriteMemory(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			string dataStr = input["data"]?.GetValue<string>() ?? "";
			MemoryType memType = ParseMemoryType(input["memory_type"]?.GetValue<string>() ?? "cpu");

			byte[] data = ParseHexBytes(dataStr);
			if(data.Length == 0) return "No data to write (provide bytes as hex pairs, e.g. \"A9 00 60\").";

			int memSize = DebugApi.GetMemorySize(memType);
			if(memSize <= 0) return $"Memory type not available.";
			if(addr + data.Length > memSize)
				return $"Write would exceed memory size ({memSize} bytes). Truncate or adjust address.";

			DebugApi.SetMemoryValues(memType, addr, data, data.Length);
			return $"Wrote {data.Length} byte(s) to {memType}:${addr:X6}.";
		}

		// ── CPU state implementations ─────────────────────────────────────────

		private static string DoGetCpuState()
		{
			if(DebugApi.GetMemorySize(MemoryType.SnesPrgRom) <= 0)
				return "No SNES ROM loaded.";

			var s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			var f = s.PS;
			bool n = (f & SnesCpuFlags.Negative)   != 0;
			bool v = (f & SnesCpuFlags.Overflow)    != 0;
			bool m = (f & SnesCpuFlags.MemoryMode8) != 0;
			bool x = (f & SnesCpuFlags.IndexMode8)  != 0;
			bool d = (f & SnesCpuFlags.Decimal)     != 0;
			bool i = (f & SnesCpuFlags.IrqDisable)  != 0;
			bool z = (f & SnesCpuFlags.Zero)        != 0;
			bool c = (f & SnesCpuFlags.Carry)       != 0;

			return $"PC: ${s.K:X2}:{s.PC:X4}  SP: ${s.SP:X4}  D: ${s.D:X4}  DBR: ${s.DBR:X2}\n" +
			       $"A:  ${s.A:X4}  X: ${s.X:X4}  Y: ${s.Y:X4}\n" +
			       $"PS: ${(byte)s.PS:X2}  N={n.B()} V={v.B()} M={m.B()} X={x.B()} D={d.B()} I={i.B()} Z={z.B()} C={c.B()}\n" +
			       $"Mode: {(s.EmulationMode ? "6502 Emulation" : "65816 Native")}  Stop: {s.StopState}";
		}

		private static string DoSetCpuRegisters(JsonObject input)
		{
			if(!EmuApi.IsPaused())
				return "Error: emulation must be paused before setting CPU registers. Call pause_emulation first.";
			if(DebugApi.GetMemorySize(MemoryType.SnesPrgRom) <= 0)
				return "No SNES ROM loaded.";

			var s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			var changed = new List<string>();

			if(input["a"]   != null) { s.A   = (ushort)input["a"]!.GetValue<int>();   changed.Add($"A=${s.A:X4}"); }
			if(input["x"]   != null) { s.X   = (ushort)input["x"]!.GetValue<int>();   changed.Add($"X=${s.X:X4}"); }
			if(input["y"]   != null) { s.Y   = (ushort)input["y"]!.GetValue<int>();   changed.Add($"Y=${s.Y:X4}"); }
			if(input["sp"]  != null) { s.SP  = (ushort)input["sp"]!.GetValue<int>();  changed.Add($"SP=${s.SP:X4}"); }
			if(input["d"]   != null) { s.D   = (ushort)input["d"]!.GetValue<int>();   changed.Add($"D=${s.D:X4}"); }
			if(input["pc"]  != null) { s.PC  = (ushort)input["pc"]!.GetValue<int>();  changed.Add($"PC=${s.PC:X4}"); }
			if(input["k"]   != null) { s.K   = (byte)input["k"]!.GetValue<int>();     changed.Add($"K=${s.K:X2}"); }
			if(input["dbr"] != null) { s.DBR = (byte)input["dbr"]!.GetValue<int>();   changed.Add($"DBR=${s.DBR:X2}"); }
			if(input["ps"]  != null) { s.PS  = (SnesCpuFlags)input["ps"]!.GetValue<int>(); changed.Add($"PS=${(byte)s.PS:X2}"); }

			if(changed.Count == 0) return "No register fields provided — nothing changed.";
			DebugApi.SetCpuState(s, CpuType.Snes);
			return $"CPU registers updated: {string.Join(", ", changed)}.";
		}

		// ── Stepping & run control implementations ───────────────────────────

		private string DoStep(StepType type, int count)
		{
			if(DebugApi.GetMemorySize(_romMemType) <= 0) return "No ROM loaded.";
			// PPU step types must target the console's main CPU (important for SA-1/SPC sub-CPUs).
			CpuType target = type is StepType.PpuStep or StepType.PpuScanline or StepType.PpuFrame
				? _cpu.GetConsoleType().GetMainCpuType()
				: _cpu;
			DebugApi.Step(target, count, type);
			string label = type switch {
				StepType.Step         => "Step into",
				StepType.StepOver     => "Step over",
				StepType.StepOut      => "Step out",
				StepType.StepBack     => count switch {
					(int)StepBackType.Instruction => "Step back (instruction)",
					(int)StepBackType.Scanline    => "Step back (scanline)",
					(int)StepBackType.Frame       => "Step back (frame)",
					_                             => "Step back"
				},
				StepType.CpuCycleStep => "Run CPU cycle",
				StepType.PpuStep      => "Run PPU cycle",
				StepType.PpuFrame     => "Run one frame",
				StepType.RunToNmi     => "Run to NMI",
				StepType.RunToIrq     => "Run to IRQ",
				_ => type.ToString()
			};
			return $"{label} executed. Waiting for emulator to pause…";
		}

		private string DoRunToScanline(JsonObject input)
		{
			if(DebugApi.GetMemorySize(_romMemType) <= 0) return "No ROM loaded.";
			int scanline = Math.Clamp(input["scanline"]?.GetValue<int>() ?? 0, 0, 999);
			DebugApi.Step(_cpu.GetConsoleType().GetMainCpuType(), scanline, StepType.SpecificScanline);
			return $"Running to scanline {scanline}…";
		}

		private string DoBreakIn(JsonObject input)
		{
			if(DebugApi.GetMemorySize(_romMemType) <= 0) return "No ROM loaded.";
			int count = Math.Max(1, input["count"]?.GetValue<int>() ?? 1);
			string typeStr = input["type"]?.GetValue<string>() ?? "instruction";
			StepType stepType = typeStr switch {
				"cpu_cycle"    => StepType.CpuCycleStep,
				"ppu_cycle"    => StepType.PpuStep,
				"ppu_scanline" => StepType.PpuScanline,
				"ppu_frame"    => StepType.PpuFrame,
				_              => StepType.Step
			};
			CpuType target = stepType is StepType.PpuStep or StepType.PpuScanline or StepType.PpuFrame
				? _cpu.GetConsoleType().GetMainCpuType()
				: _cpu;
			DebugApi.Step(target, count, stepType);
			return $"Break in: advancing {count} × {typeStr}…";
		}

		// ── Watch implementations ─────────────────────────────────────────────

		private string DoAddWatch(JsonObject input)
		{
			var arr = input["expressions"]?.AsArray();
			if(arr == null || arr.Count == 0) return "No expressions provided.";

			var manager = WatchManager.GetWatchManager(_cpu);
			var added = new List<string>();
			foreach(var node in arr) {
				string expr = node?.GetValue<string>() ?? "";
				if(expr.Length == 0) continue;
				manager.AddWatch(expr);
				added.Add(expr);
			}
			return added.Count == 0
				? "No valid expressions provided."
				: $"Added {added.Count} watch(es): {string.Join(", ", added.Select(e => $"'{e}'"))}.";
		}

		private string DoGetWatches()
		{
			var manager = WatchManager.GetWatchManager(_cpu);
			var entries = manager.WatchEntries;
			if(entries.Count == 0) return "No watches defined. Use add_watch to add expressions.";

			// Evaluate current values
			var values = manager.GetWatchContent(new List<WatchValueInfo>());
			var sb = new StringBuilder();
			sb.AppendLine($"{entries.Count} watch(es):");
			for(int i = 0; i < entries.Count; i++) {
				string val = i < values.Count ? values[i].Value : "?";
				sb.AppendLine($"  [{i}]  {entries[i]}  =  {val}");
			}
			return sb.ToString();
		}

		private string DoRemoveWatch(JsonObject input)
		{
			var arr = input["indexes"]?.AsArray();
			if(arr == null || arr.Count == 0) return "No indexes provided.";

			var manager = WatchManager.GetWatchManager(_cpu);
			int total = manager.WatchEntries.Count;
			var indexes = arr.Select(n => n?.GetValue<int>() ?? -1).Where(i => i >= 0 && i < total).ToArray();
			if(indexes.Length == 0) return $"No valid indexes (current watch count: {total}).";

			manager.RemoveWatch(indexes);
			return $"Removed {indexes.Length} watch(es) at index(es): {string.Join(", ", indexes)}.";
		}

		// ── New CDL / annotation tools ────────────────────────────────────────

		private string DoGetUnlabeledFunctions(JsonObject input)
		{
			int romSize = DebugApi.GetMemorySize(_romMemType);
			if(romSize <= 0) return "No ROM loaded.";

			string filter   = input["filter"]?.GetValue<string>() ?? "either";
			string? bankStr = input["bank"]?.GetValue<string>()?.TrimStart('$');
			int? filterBank = bankStr != null ? Convert.ToInt32(bankStr, 16) : (int?)null;
			int maxResults  = Math.Clamp(input["max_results"]?.GetValue<int>() ?? 200, 1, 1000);

			var functions = DebugApi.GetCdlFunctions(_romMemType);
			var cdl = DebugApi.GetCdlData(0, (uint)romSize, _romMemType);

			var sb = new StringBuilder();
			int count = 0;

			foreach(var romOffset in functions) {
				if(count >= maxResults) break;

				var rel = DebugApi.GetRelativeAddress(new AddressInfo { Address = (int)romOffset, Type = _romMemType }, _cpu);
				if(rel.Address < 0) continue;
				uint snesAddr = (uint)rel.Address;

				if(filterBank.HasValue && (snesAddr >> 16) != (uint)filterBank.Value) continue;

				var lbl    = LabelManager.GetLabel(new AddressInfo { Address = (int)romOffset, Type = _romMemType });
				string name    = lbl?.Label   ?? "";
				string comment = lbl?.Comment ?? "";

				bool noLabel   = string.IsNullOrEmpty(name);
				bool noComment = string.IsNullOrEmpty(comment);
				bool include   = filter switch {
					"no_label"   => noLabel,
					"no_comment" => noComment,
					"both"       => noLabel && noComment,
					_            => noLabel || noComment  // "either" (default)
				};
				if(!include) continue;

				CdlFlags flags = romOffset < (uint)cdl.Length ? cdl[romOffset] : CdlFlags.None;
				sb.AppendLine($"  ${snesAddr:X6}  ROM+${romOffset:X6}  [{FormatCdlFlags(flags)}]  label='{name}'  comment='{comment}'");
				count++;
			}

			string header = $"Functions matching filter='{filter}'" +
			                (filterBank.HasValue ? $" bank=${filterBank:X2}" : "") +
			                $": {count} of {functions.Length} total CDL functions shown" +
			                (count >= maxResults ? $" (capped at {maxResults}; use max_results to increase)" : "") +
			                "\n";
			return header + (count == 0 ? "  (none match filter)" : sb.ToString());
		}

		private string DoGetCdlFunctionsPaged(JsonObject input)
		{
			int romSize = DebugApi.GetMemorySize(_romMemType);
			if(romSize <= 0) return "No ROM loaded.";

			string? bankRaw = input["bank"]?.GetValue<string>()?.TrimStart('$');
			if(string.IsNullOrEmpty(bankRaw)) return "Error: 'bank' parameter is required (e.g. \"$80\").";
			int filterBank = Convert.ToInt32(bankRaw, 16);

			int page     = Math.Max(0, input["page"]?.GetValue<int>() ?? 0);
			int pageSize = Math.Clamp(input["page_size"]?.GetValue<int>() ?? 50, 1, 200);
			string filter = input["filter"]?.GetValue<string>() ?? "all";

			var functions = DebugApi.GetCdlFunctions(_romMemType);
			var cdl = DebugApi.GetCdlData(0, (uint)romSize, _romMemType);

			var entries = new List<(uint romOffset, uint snesAddr, CdlFlags flags, string name, string comment)>();
			foreach(var romOffset in functions) {
				var rel = DebugApi.GetRelativeAddress(new AddressInfo { Address = (int)romOffset, Type = _romMemType }, _cpu);
				if(rel.Address < 0) continue;
				uint snesAddr = (uint)rel.Address;
				if((snesAddr >> 16) != (uint)filterBank) continue;

				var lbl    = LabelManager.GetLabel(new AddressInfo { Address = (int)romOffset, Type = _romMemType });
				string name    = lbl?.Label   ?? "";
				string comment = lbl?.Comment ?? "";

				bool noLabel   = string.IsNullOrEmpty(name);
				bool noComment = string.IsNullOrEmpty(comment);
				bool include   = filter switch {
					"no_label"    => noLabel,
					"no_comment"  => noComment,
					"unannotated" => noLabel && noComment,
					"labeled"     => !noLabel,
					_             => true  // "all"
				};
				if(!include) continue;

				CdlFlags flags = romOffset < (uint)cdl.Length ? cdl[romOffset] : CdlFlags.None;
				entries.Add((romOffset, snesAddr, flags, name, comment));
			}

			int totalFiltered = entries.Count;
			int totalPages    = totalFiltered == 0 ? 1 : (totalFiltered + pageSize - 1) / pageSize;
			int startIdx      = page * pageSize;
			var pageEntries   = entries.Skip(startIdx).Take(pageSize).ToList();

			var sb = new StringBuilder();
			sb.AppendLine($"Bank ${filterBank:X2} CDL functions (filter='{filter}'): {totalFiltered} entries, " +
			              $"page {page} of {totalPages - 1}, showing {pageEntries.Count}");
			sb.AppendLine();

			if(pageEntries.Count == 0) {
				sb.AppendLine("  (no entries on this page)");
			} else {
				foreach(var (romOffset, snesAddr, flags, name, comment) in pageEntries) {
					string commentStr = comment.Length > 0 ? $"  ; {comment}" : "";
					sb.AppendLine($"  ${snesAddr:X6}  ROM+${romOffset:X6}  [{FormatCdlFlags(flags)}]  {name}{commentStr}");
				}
			}

			if(totalPages > 1) {
				sb.AppendLine();
				if(page < totalPages - 1)
					sb.AppendLine($"[Next page: use page={page + 1}]");
				else
					sb.AppendLine("[Last page]");
			}

			return sb.ToString();
		}

		private string DoSetDataType(JsonObject input)
		{
			int romSize = DebugApi.GetMemorySize(_romMemType);
			if(romSize <= 0) return "No ROM loaded.";

			uint addr   = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			int  length = Math.Clamp(input["length"]?.GetValue<int>() ?? 1, 1, 65536);
			string type = (input["type"]?.GetValue<string>() ?? "none").ToLowerInvariant();

			var absAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)addr, Type = _cpuMemType });
			if(absAddr.Type == MemoryType.None || absAddr.Address < 0)
				return $"Error: address ${addr:X6} does not map to ROM.";

			CdlFlags flags = type switch {
				"code"        => CdlFlags.Code,
				"data"        => CdlFlags.Data,
				"jump_target" => CdlFlags.JumpTarget,
				"sub_entry"   => CdlFlags.SubEntryPoint,
				"none"        => CdlFlags.None,
				_ => throw new ArgumentException($"Unknown type '{type}'. Valid: code, data, jump_target, sub_entry, none.")
			};

			uint start = (uint)absAddr.Address;
			uint end   = (uint)Math.Min(absAddr.Address + length - 1, romSize - 1);

			DebugApi.MarkBytesAs(_romMemType, start, end, flags);
			return $"Marked ${addr:X6} (ROM ${start:X6}–${end:X6}, {end - start + 1} bytes) as '{type}'.";
		}

		// ── Context block (CPU state + PC disassembly) ────────────────────────
		// Appended to every tool response so the AI never needs a separate
		// get_cpu_state or get_disassembly call just to see where execution is.

		private string GetContextBlock()
		{
			if(DebugApi.GetMemorySize(_romMemType) <= 0) return "";

			var s  = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			var f  = s.PS;
			bool n  = (f & SnesCpuFlags.Negative)   != 0;
			bool v  = (f & SnesCpuFlags.Overflow)    != 0;
			bool m  = (f & SnesCpuFlags.MemoryMode8) != 0;
			bool xi = (f & SnesCpuFlags.IndexMode8)  != 0;
			bool dl = (f & SnesCpuFlags.Decimal)     != 0;
			bool ii = (f & SnesCpuFlags.IrqDisable)  != 0;
			bool z  = (f & SnesCpuFlags.Zero)        != 0;
			bool c  = (f & SnesCpuFlags.Carry)       != 0;

			bool paused = EmuApi.IsPaused();
			var sb = new StringBuilder();
			sb.AppendLine();
			sb.AppendLine($"--- CPU State ({(paused ? "paused" : "running snapshot")}) ---");
			sb.AppendLine($"PC=${s.K:X2}:{s.PC:X4}  A=${s.A:X4}  X=${s.X:X4}  Y=${s.Y:X4}  SP=${s.SP:X4}  D=${s.D:X4}  DBR=${s.DBR:X2}");
			sb.AppendLine($"PS=${( byte)f:X2}  N={n.B()} V={v.B()} M={m.B()} X={xi.B()} D={dl.B()} I={ii.B()} Z={z.B()} C={c.B()}  Mode={(s.EmulationMode ? "Emulation" : "Native")}");

			uint pcAddr = ((uint)s.K << 16) | s.PC;
			AppendDisasmAtPc(sb, _cpu, pcAddr);

			return sb.ToString();
		}

		/// <summary>
		/// Builds a snapshot of CPU state + disassembly for any supported CPU type.
		/// Called at the moment a breakpoint fires so the context is captured immediately.
		/// </summary>
		public string BuildBreakContext(CpuType cpu)
		{
			if(DebugApi.GetMemorySize(_romMemType) <= 0) return "";

			var sb = new StringBuilder();
			sb.AppendLine($"--- {cpu} CPU State (breakpoint) ---");

			switch(cpu) {
				case CpuType.Snes:
				case CpuType.Sa1: {
					var s  = DebugApi.GetCpuState<SnesCpuState>(cpu);
					var f  = s.PS;
					bool n  = (f & SnesCpuFlags.Negative)   != 0;
					bool v  = (f & SnesCpuFlags.Overflow)    != 0;
					bool m  = (f & SnesCpuFlags.MemoryMode8) != 0;
					bool xi = (f & SnesCpuFlags.IndexMode8)  != 0;
					bool dl = (f & SnesCpuFlags.Decimal)     != 0;
					bool ii = (f & SnesCpuFlags.IrqDisable)  != 0;
					bool z  = (f & SnesCpuFlags.Zero)        != 0;
					bool c  = (f & SnesCpuFlags.Carry)       != 0;
					sb.AppendLine($"PC=${s.K:X2}:{s.PC:X4}  A=${s.A:X4}  X=${s.X:X4}  Y=${s.Y:X4}  SP=${s.SP:X4}  D=${s.D:X4}  DBR=${s.DBR:X2}");
					sb.AppendLine($"PS=${( byte)f:X2}  N={n.B()} V={v.B()} M={m.B()} X={xi.B()} D={dl.B()} I={ii.B()} Z={z.B()} C={c.B()}  Mode={(s.EmulationMode ? "Emulation" : "Native")}");
					AppendDisasmAtPc(sb, cpu, ((uint)s.K << 16) | s.PC);
					break;
				}
				case CpuType.Gsu: {
					var s = DebugApi.GetCpuState<GsuState>(cpu);
					sb.AppendLine($"PC=${s.ProgramBank:X2}:{s.R[15]:X4}  RomBank=${s.RomBank:X2}  RamBank=${s.RamBank:X2}");
					sb.Append("Regs: ");
					for(int i = 0; i < 16; i++) sb.Append($"R{i}=${s.R[i]:X4} ");
					sb.AppendLine();
					sb.AppendLine($"SFR: Z={s.SFR.Zero.B()} C={s.SFR.Carry.B()} S={s.SFR.Sign.B()} OV={s.SFR.Overflow.B()} Running={s.SFR.Running.B()}");
					AppendDisasmAtPc(sb, cpu, ((uint)s.ProgramBank << 16) | s.R[15]);
					break;
				}
				case CpuType.NecDsp: {
					var s = DebugApi.GetCpuState<NecDspState>(cpu);
					sb.AppendLine($"PC=${s.PC:X4}  A=${s.A:X4}  B=${s.B:X4}  TR=${s.TR:X4}  DP=${s.DP:X4}  SP=${s.SP:X2}");
					AppendDisasmAtPc(sb, cpu, s.PC);
					break;
				}
				default:
					sb.AppendLine($"(No register formatter for {cpu})");
					break;
			}

			return sb.ToString();
		}

		private static void AppendDisasmAtPc(StringBuilder sb, CpuType cpu, uint pcAddr)
		{
			var rows = DebugApi.GetDisassemblyOutput(cpu, pcAddr, 6);
			if(rows.Length == 0) return;
			sb.AppendLine("Disasm at PC:");
			foreach(var row in rows) {
				string marker  = (row.Address >= 0 && (uint)row.Address == pcAddr) ? "▶" : " ";
				string addrStr = row.Address >= 0 ? $"${row.Address:X6}" : "      ";
				string cmt     = row.Comment.Length > 0 ? $"  ; {row.Comment}" : "";
				sb.AppendLine($"  {marker}{addrStr}  {row.Text.PadRight(24)}{cmt}");
			}
		}

		private string DoGetPendingBreakpoints()
		{
			var entries = GetAndClearPendingBreaks?.Invoke();
			if(entries == null || entries.Count == 0)
				return "No pending breakpoints.";

			var sb = new StringBuilder();
			sb.AppendLine($"{entries.Count} pending breakpoint(s):");
			foreach(var entry in entries) {
				sb.AppendLine("---");
				sb.AppendLine(entry);
			}
			return sb.ToString();
		}

		// ── Shared CDL flag formatter ─────────────────────────────────────────

		private static string FormatCdlFlags(CdlFlags f)
		{
			var parts = new List<string>();
			if((f & CdlFlags.Code)          != 0) parts.Add("Code");
			if((f & CdlFlags.Data)          != 0) parts.Add("Data");
			if((f & CdlFlags.JumpTarget)    != 0) parts.Add("JumpTarget");
			if((f & CdlFlags.SubEntryPoint) != 0) parts.Add("SubEntry");
			if((f & CdlFlags.IndexMode8)    != 0) parts.Add("X8");
			if((f & CdlFlags.MemoryMode8)   != 0) parts.Add("M8");
			return parts.Count > 0 ? string.Join("|", parts) : "None";
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private static uint ParseAddress(string s)
		{
			s = s.Trim().TrimStart('$');
			if(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				s = s.Substring(2);
			return Convert.ToUInt32(s, 16);
		}

		private static MemoryType ParseMemoryType(string s) => s switch {
			"prg_rom"  => MemoryType.SnesPrgRom,
			"work_ram" => MemoryType.SnesWorkRam,
			"save_ram" => MemoryType.SnesSaveRam,
			"vram"     => MemoryType.SnesVideoRam,
			"oam"      => MemoryType.SnesSpriteRam,
			"cgram"    => MemoryType.SnesCgRam,
			_          => MemoryType.SnesMemory
		};

		private static byte[] ParseHexBytes(string s)
		{
			// Strip separators and 0x prefixes, then group into byte pairs
			s = s.Replace("0x", "").Replace(",", "").Replace(" ", "").Trim();
			if(s.Length % 2 != 0) s = "0" + s;  // pad odd nibble
			var result = new byte[s.Length / 2];
			for(int i = 0; i < result.Length; i++)
				result[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
			return result;
		}
	}
}

// Extension to format bool as "0"/"1" for flag display
internal static class BoolExt
{
	public static string B(this bool v) => v ? "1" : "0";
}
