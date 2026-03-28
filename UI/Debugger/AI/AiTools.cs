using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;
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

		// Review queue ref injected so set_review_queue tool can add items
		public ExecutionMonitor? Monitor { get; set; }

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
				"Get all current labels as a list of {address, memory_type, name, comment} objects.",
				new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

			Tool("get_label_at",
				"Get the label and comment at a specific SNES CPU address, if any.",
				Schema("", new() {
					["address"] = Prop("string", "SNES CPU address as hex string")
				}, new[] { "address" })),

			Tool("set_label",
				"Set a label name and/or comment at a SNES CPU address. Overwrites any existing label at that address.",
				Schema("", new() {
					["address"] = Prop("string", "SNES CPU address as hex string"),
					["name"] = Prop("string", "Label name (alphanumeric + underscore + @, max 100 chars). Empty string to keep existing name."),
					["comment"] = Prop("string", "Comment text. Empty string to clear comment.")
				}, new[] { "address", "name" })),

			Tool("delete_label",
				"Delete the label at a SNES CPU address.",
				Schema("", new() {
					["address"] = Prop("string", "SNES CPU address as hex string")
				}, new[] { "address" })),

			Tool("get_call_stack",
				"Get the current CPU call stack showing active subroutine chain.",
				new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

			Tool("get_annotation_summary",
				"Get high-level statistics: total ROM bytes, CDL-covered code bytes, labeled functions, unannotated jump targets, etc.",
				new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

			Tool("add_to_review_queue",
				"Queue a SNES CPU address for later manual review with a reason note. Use this when you encounter code that needs more execution context before it can be annotated.",
				Schema("", new() {
					["address"] = Prop("string", "SNES CPU address as hex string"),
					["reason"] = Prop("string", "Why this address needs review")
				}, new[] { "address", "reason" })),

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
				["type"] = Prop("string", ""soft" = SNES reset button (default); "hard" = power cycle (full restart)")
			}, Array.Empty<string>())),

		// ── Breakpoints ────────────────────────────────────────────────────

		Tool("list_breakpoints",
			"List all currently active breakpoints.",
			new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

		Tool("set_breakpoint",
			"Add a breakpoint. Emulation pauses when the condition is met.",
			Schema("", new() {
				["address"]     = Prop("string",  "SNES CPU address as hex string ($BBAAAA)"),
				["break_on"]    = Prop("string",  ""exec" (default), "read", "write", "read_write", or "all""),
				["memory_type"] = Prop("string",  ""cpu" (default, CPU bus), "work_ram", "save_ram", "vram", "oam", "cgram""),
				["condition"]   = Prop("string",  "Optional condition expression (e.g. "A == $10")")
			}, new[] { "address" })),

		Tool("remove_breakpoint",
			"Remove all breakpoints at the given address.",
			Schema("", new() {
				["address"]     = Prop("string", "SNES CPU address as hex string"),
				["memory_type"] = Prop("string", "Memory space (default: "cpu")")
			}, new[] { "address" })),

		// ── Memory write ───────────────────────────────────────────────────

		Tool("write_memory",
			"Write bytes to emulated memory. Takes effect immediately, even while running.",
			Schema("", new() {
				["address"]     = Prop("string", "Start address as hex string"),
				["data"]        = Prop("string", "Bytes as space-separated hex pairs, e.g. "A9 00 85 7E""),
				["memory_type"] = Prop("string", ""cpu" (default), "work_ram", "save_ram", "vram", "oam", "cgram", "prg_rom"")
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
		};

		// ── Tool executor ─────────────────────────────────────────────────────

		public async Task<string> ExecuteAsync(string toolName, JsonObject input)
		{
			// All DebugApi/LabelManager calls must run on the UI thread
			return await Dispatcher.UIThread.InvokeAsync(() => Execute(toolName, input));
		}

		private string Execute(string name, JsonObject input)
		{
			try {
				return name switch {
					"get_disassembly" => DoGetDisassembly(input),
					"read_memory" => DoReadMemory(input),
					"get_cdl_data" => DoGetCdlData(input),
					"get_labels" => DoGetLabels(),
					"get_label_at" => DoGetLabelAt(input),
					"set_label" => DoSetLabel(input),
					"delete_label" => DoDeleteLabel(input),
					"get_call_stack" => DoGetCallStack(),
					"get_annotation_summary" => DoGetAnnotationSummary(),
					"add_to_review_queue"  => DoAddToReviewQueue(input),
					"pause_emulation"      => DoPauseEmulation(),
					"resume_emulation"     => DoResumeEmulation(),
					"reset_game"           => DoResetGame(input),
					"list_breakpoints"     => DoListBreakpoints(),
					"set_breakpoint"       => DoSetBreakpoint(input),
					"remove_breakpoint"    => DoRemoveBreakpoint(input),
					"write_memory"         => DoWriteMemory(input),
					"get_cpu_state"        => DoGetCpuState(),
					"set_cpu_registers"    => DoSetCpuRegisters(input),
					_ => $"Unknown tool: {name}"
				};
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
			for(int i = 0; i < flags.Length; i++) {
				var f = flags[i];
				var parts = new List<string>();
				if((f & CdlFlags.Code) != 0) parts.Add("Code");
				if((f & CdlFlags.Data) != 0) parts.Add("Data");
				if((f & CdlFlags.JumpTarget) != 0) parts.Add("JumpTarget");
				if((f & CdlFlags.SubEntryPoint) != 0) parts.Add("SubEntry");
				if((f & CdlFlags.IndexMode8) != 0) parts.Add("X8");
				if((f & CdlFlags.MemoryMode8) != 0) parts.Add("M8");
				string flagStr = parts.Count > 0 ? string.Join("|", parts) : "None";
				sb.AppendLine($"  +{i:X4} (offset ${romOffset + i:X6}): {flagStr}");
			}
			return sb.ToString();
		}

		private string DoGetLabels()
		{
			var labels = LabelManager.GetAllLabels();
			if(labels.Count == 0) return "No labels defined.";

			var sb = new StringBuilder();
			sb.AppendLine($"{labels.Count} label(s):");
			foreach(var lbl in labels.OrderBy(l => l.Address)) {
				string comment = lbl.Comment.Length > 0 ? $"  ; {lbl.Comment}" : "";
				sb.AppendLine($"  {lbl.MemoryType}:${lbl.Address:X6}  {lbl.Label}{comment}");
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
			return $"Address: ${addr:X6} (ROM ${absAddr.Address:X6})\nName: {label.Label}\nComment: {label.Comment}";
		}

		private string DoSetLabel(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			string name = input["name"]?.GetValue<string>() ?? "";
			string comment = input["comment"]?.GetValue<string>() ?? "";

			if(!string.IsNullOrEmpty(name) && !LabelManager.LabelRegex.IsMatch(name))
				return $"Invalid label name '{name}'. Use only letters, digits, underscore, @.";

			var absAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)addr, Type = _cpuMemType });
			if(absAddr.Type == MemoryType.None || absAddr.Address < 0)
				return $"Address ${addr:X6} does not map to ROM.";

			// If name is empty, keep existing name
			if(string.IsNullOrEmpty(name)) {
				var existing = LabelManager.GetLabel(absAddr);
				name = existing?.Label ?? "";
			}

			LabelManager.SetLabel(new CodeLabel {
				Address = (uint)absAddr.Address,
				MemoryType = absAddr.Type,
				Label = name,
				Comment = comment,
				Length = 1
			}, raiseEvent: true);
			DebugWorkspaceManager.AutoSave();
			return $"Label set: ${addr:X6} → name='{name}' comment='{comment}'";
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
			int romLabels = labels.Count(l => l.MemoryType == _romMemType);

			// Count unannotated CDL targets
			var cdl = DebugApi.GetCdlData(0, (uint)romSize, _romMemType);
			int unannotatedTargets = 0;
			for(int i = 0; i < cdl.Length; i++) {
				bool isTarget = (cdl[i] & (CdlFlags.JumpTarget | CdlFlags.SubEntryPoint)) != 0;
				if(isTarget) {
					var abs = new AddressInfo { Address = i, Type = _romMemType };
					if(LabelManager.GetLabel(abs) == null) unannotatedTargets++;
				}
			}

			double codePct = romSize > 0 ? 100.0 * stats.CodeBytes / romSize : 0;
			double dataPct = romSize > 0 ? 100.0 * stats.DataBytes / romSize : 0;

			return $"ROM size: {romSize:N0} bytes\n" +
			       $"CDL coverage: {stats.CodeBytes:N0} code bytes ({codePct:F1}%), {stats.DataBytes:N0} data bytes ({dataPct:F1}%)\n" +
			       $"CDL functions: {stats.FunctionCount:N0}  jump targets: {stats.JumpTargetCount:N0}\n" +
			       $"Labels: {romLabels:N0} on ROM\n" +
			       $"Unannotated CDL targets: {unannotatedTargets:N0}";
		}

		private string DoAddToReviewQueue(JsonObject input)
		{
			uint addr = ParseAddress(input["address"]?.GetValue<string>() ?? "0");
			string reason = input["reason"]?.GetValue<string>() ?? "";
			var absAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)addr, Type = _cpuMemType });

			Monitor?.AddToQueue(new ReviewQueueItem {
				CpuAddress = addr,
				RomOffset = absAddr.Address >= 0 ? (uint)absAddr.Address : 0,
				Reason = reason,
				Source = ReviewQueueItemSource.AiRequested
			});
			return $"Added ${addr:X6} to review queue: {reason}";
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
			string breakOn = input["break_on"]?.GetValue<string>() ?? "exec";
			string condition = input["condition"]?.GetValue<string>() ?? "";
			MemoryType memType = ParseMemoryType(input["memory_type"]?.GetValue<string>() ?? "cpu");

			var bp = new Breakpoint {
				StartAddress = addr,
				EndAddress = addr,
				MemoryType = memType,
				CpuType = CpuType.Snes,
				Enabled = true,
				BreakOnExec  = breakOn is "exec" or "all",
				BreakOnRead  = breakOn is "read" or "read_write" or "all",
				BreakOnWrite = breakOn is "write" or "read_write" or "all",
				Condition = condition
			};

			BreakpointManager.AddBreakpoint(bp);
			return $"Breakpoint set at ${addr:X6} ({memType}, {breakOn}).";
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
