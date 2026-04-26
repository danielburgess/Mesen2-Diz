using Avalonia.Threading;
using Mesen.Config;
using Mesen.Config.Shortcuts;
using Mesen.Debugger;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Utilities
{
	public static class IpcCommandHandler
	{
		// Command registry — keep in lockstep with the dispatcher switch below.
		// Category order and grouping mirror the switch for quick cross-check.
		private static readonly (string Category, string[] Commands)[] _commandRegistry = new[] {
			("Labels", new[] { "setLabel", "setLabels", "deleteLabel", "getLabel", "getLabelByName", "getAllLabels" }),
			("Memory", new[] { "readMemory", "writeMemory", "getMemorySize" }),
			("CPU State", new[] { "getCpuState", "setCpuState", "getProgramCounter", "setProgramCounter" }),
			("Execution Control", new[] { "pause", "resume", "isPaused", "step", "stepTrace" }),
			("Disassembly", new[] { "getDisassembly", "searchDisassembly" }),
			("Breakpoints", new[] { "addBreakpoint", "removeBreakpoint", "getBreakpoints", "clearBreakpoints" }),
			("Expression Evaluation", new[] { "evaluate" }),
			("Call Stack", new[] { "getCallstack" }),
			("Code/Data Log", new[] { "getCdlData", "getCdlStatistics", "getCdlFunctions" }),
			("Address Mapping", new[] { "getAbsoluteAddress", "getRelativeAddress" }),
			("ROM Info", new[] { "getRomInfo", "getStatus" }),
			("Screenshot", new[] { "takeScreenshot" }),
			("Emulator control", new[] { "loadRom", "reloadRom", "powerCycle", "powerOff", "reset" }),
			("Save states", new[] { "saveStateSlot", "loadStateSlot", "saveStateFile", "loadStateFile" }),
			("Controller input override", new[] { "setControllerInput", "clearControllerInput" }),
			("Emulation settings", new[] { "getEmulationSpeed", "setEmulationSpeed", "getTurboSpeed", "setTurboSpeed", "getRunAheadFrames", "setRunAheadFrames", "getConfig" }),
			("Timing & PPU", new[] { "getTimingInfo", "getPpuState" }),
			("SPC / DSP", new[] { "getSpcState", "getDspState" }),
			("IPC info", new[] { "getIpcInfo" }),
			("Cheats", new[] { "setCheats", "clearCheats" }),
			("PPU memory", new[] { "getVram", "getCgram", "getOam" }),
			("PPU / DMA state", new[] { "getBgState", "getDmaState" }),
			("Tilemap / graphics decode", new[] { "getTilemap", "decodeTiles", "renderBgLayer" }),
			("Targeted execution", new[] { "runUntilVramWrite" }),
			("Memory search & diff", new[] { "searchMemory", "snapshotMemory", "diffMemory", "clearSnapshots" }),
			("Trace log & event wait", new[] { "getTraceLog", "setTraceLogEnabled", "waitForEvent" }),
			("IPC memory watch hook", new[] { "watchCpuMemory", "addCpuMemoryWatch", "clearCpuMemoryWatches", "pollMemoryEvents", "setMemoryWatchEnabled", "setMemoryWatchRingSize" }),
			("Introspection", new[] { "listCommands", "getCommands" }),
		};

		private static string CmdListCommands()
		{
			var categories = new JsonObject();
			var flat = new JsonArray();
			int count = 0;
			foreach(var (cat, cmds) in _commandRegistry) {
				var arr = new JsonArray();
				foreach(string c in cmds) {
					arr.Add((JsonNode)JsonValue.Create(c)!);
					flat.Add((JsonNode)JsonValue.Create(c)!);
					count++;
				}
				categories[cat] = arr;
			}
			return Ok(new JsonObject {
				["count"] = count,
				["commands"] = flat,
				["categories"] = categories
			});
		}

		public static string HandleCommand(string json)
		{
			JsonNode? root;
			try {
				root = JsonNode.Parse(json);
			} catch {
				return Error("Invalid JSON");
			}

			if(root == null) return Error("Empty request");

			string? cmd = root["command"]?.GetValue<string>();
			if(string.IsNullOrEmpty(cmd)) return Error("Missing 'command' field");

			try {
				return cmd switch {
					// Labels
					"setLabel" => CmdSetLabel(root),
					"setLabels" => CmdSetLabels(root),
					"deleteLabel" => CmdDeleteLabel(root),
					"getLabel" => CmdGetLabel(root),
					"getLabelByName" => CmdGetLabelByName(root),
					"getAllLabels" => CmdGetAllLabels(root),

					// Memory
					"readMemory" => CmdReadMemory(root),
					"writeMemory" => CmdWriteMemory(root),
					"getMemorySize" => CmdGetMemorySize(root),

					// CPU State
					"getCpuState" => CmdGetCpuState(root),
					"setCpuState" => CmdSetCpuState(root),
					"getProgramCounter" => CmdGetProgramCounter(root),
					"setProgramCounter" => CmdSetProgramCounter(root),

					// Execution Control
					"pause" => CmdPause(),
					"resume" => CmdResume(),
					"isPaused" => CmdIsPaused(),
					"step" => CmdStep(root),
					"stepTrace" => CmdStepTrace(root),

					// Disassembly
					"getDisassembly" => CmdGetDisassembly(root),
					"searchDisassembly" => CmdSearchDisassembly(root),

					// Breakpoints
					"addBreakpoint" => CmdAddBreakpoint(root),
					"removeBreakpoint" => CmdRemoveBreakpoint(root),
					"getBreakpoints" => CmdGetBreakpoints(root),
					"clearBreakpoints" => CmdClearBreakpoints(),

					// Expression Evaluation
					"evaluate" => CmdEvaluate(root),

					// Call Stack
					"getCallstack" => CmdGetCallstack(root),

					// Code/Data Log
					"getCdlData" => CmdGetCdlData(root),
					"getCdlStatistics" => CmdGetCdlStatistics(root),
					"getCdlFunctions" => CmdGetCdlFunctions(root),

					// Address Mapping
					"getAbsoluteAddress" => CmdGetAbsoluteAddress(root),
					"getRelativeAddress" => CmdGetRelativeAddress(root),

					// ROM Info
					"getRomInfo" => CmdGetRomInfo(),
					"getStatus" => CmdGetStatus(),

					// Screenshot
					"takeScreenshot" => CmdTakeScreenshot(root),

					// Emulator control
					"loadRom" => CmdLoadRom(root),
					"reloadRom" => CmdReloadRom(),
					"powerCycle" => CmdPowerCycle(),
					"powerOff" => CmdPowerOff(),
					"reset" => CmdReset(),

					// Save states
					"saveStateSlot" => CmdSaveStateSlot(root),
					"loadStateSlot" => CmdLoadStateSlot(root),
					"saveStateFile" => CmdSaveStateFile(root),
					"loadStateFile" => CmdLoadStateFile(root),

					// Controller input override
					"setControllerInput" => CmdSetControllerInput(root),
					"clearControllerInput" => CmdClearControllerInput(root),

					// Emulation settings
					"getEmulationSpeed" => CmdGetEmulationSpeed(),
					"setEmulationSpeed" => CmdSetEmulationSpeed(root),
					"getTurboSpeed" => CmdGetTurboSpeed(),
					"setTurboSpeed" => CmdSetTurboSpeed(root),
					"getRunAheadFrames" => CmdGetRunAheadFrames(),
					"setRunAheadFrames" => CmdSetRunAheadFrames(root),
					"getConfig" => CmdGetConfig(),

					// Timing & PPU
					"getTimingInfo" => CmdGetTimingInfo(root),
					"getPpuState" => CmdGetPpuState(root),

					// SPC / DSP
					"getSpcState" => CmdGetSpcState(root),
					"getDspState" => CmdGetDspState(root),

					// IPC info
					"getIpcInfo" => CmdGetIpcInfo(),

					// Cheats
					"setCheats" => CmdSetCheats(root),
					"clearCheats" => CmdClearCheats(),

					// PPU memory
					"getVram" => CmdGetVram(root),
					"getCgram" => CmdGetCgram(root),
					"getOam" => CmdGetOam(),

					// PPU / DMA state
					"getBgState" => CmdGetBgState(root),
					"getDmaState" => CmdGetDmaState(root),

					// Tilemap / graphics decode
					"getTilemap" => CmdGetTilemap(root),
					"decodeTiles" => CmdDecodeTiles(root),
					"renderBgLayer" => CmdRenderBgLayer(root),

					// Targeted execution
					"runUntilVramWrite" => CmdRunUntilVramWrite(root),

					// Memory search & diff
					"searchMemory" => CmdSearchMemory(root),
					"snapshotMemory" => CmdSnapshotMemory(root),
					"diffMemory" => CmdDiffMemory(root),
					"clearSnapshots" => CmdClearSnapshots(),

					// Trace log & event wait
					"getTraceLog" => CmdGetTraceLog(root),
					"setTraceLogEnabled" => CmdSetTraceLogEnabled(root),
					"waitForEvent" => CmdWaitForEvent(root),

					// IPC memory watch hook
					"watchCpuMemory" => CmdWatchCpuMemory(root),
					"addCpuMemoryWatch" => CmdAddCpuMemoryWatch(root),
					"clearCpuMemoryWatches" => CmdClearCpuMemoryWatches(),
					"pollMemoryEvents" => CmdPollMemoryEvents(root),
					"setMemoryWatchEnabled" => CmdSetMemoryWatchEnabled(root),
					"setMemoryWatchRingSize" => CmdSetMemoryWatchRingSize(root),

					// Introspection
					"listCommands" or "getCommands" => CmdListCommands(),

					_ => Error($"Unknown command: {cmd}")
				};
			} catch(Exception ex) {
				return Error($"Command '{cmd}' failed: {ex.Message}");
			}
		}

		// ── Helpers ──────────────────────────────────────────────────────────

		private static string Ok(JsonNode? data = null)
		{
			var obj = new JsonObject { ["success"] = true };
			if(data != null) obj["data"] = data;
			return obj.ToJsonString();
		}

		private static string Error(string message)
		{
			return new JsonObject { ["success"] = false, ["error"] = message }.ToJsonString();
		}

		private static uint ParseHexOrDec(JsonNode? node)
		{
			if(node == null) throw new ArgumentException("Missing required address");
			string val = node.ToString();
			if(val.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || val.StartsWith("$")) {
				string hex = val.StartsWith("$") ? val[1..] : val[2..];
				return uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			}
			if(uint.TryParse(val, out uint dec)) return dec;
			return uint.Parse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		}

		private static MemoryType ParseMemoryType(JsonNode? node)
		{
			if(node == null) throw new ArgumentException("Missing 'memoryType'");
			string val = node.GetValue<string>();
			if(Enum.TryParse<MemoryType>(val, true, out var mt)) return mt;
			throw new ArgumentException($"Invalid memoryType: {val}");
		}

		private static CpuType ParseCpuType(JsonNode? node, CpuType defaultValue = CpuType.Snes)
		{
			if(node == null) return defaultValue;
			string val = node.GetValue<string>();
			if(Enum.TryParse<CpuType>(val, true, out var ct)) return ct;
			return defaultValue;
		}

		private static JsonObject LabelToJson(CodeLabel lbl)
		{
			return new JsonObject {
				["address"] = lbl.Address.ToString("X6"),
				["memoryType"] = lbl.MemoryType.ToString(),
				["label"] = lbl.Label,
				["comment"] = lbl.Comment,
				["length"] = lbl.Length,
				["category"] = lbl.Category.ToString()
			};
		}

		// Run on UI thread synchronously (needed for label operations that raise events)
		private static T RunOnUiThread<T>(Func<T> func)
		{
			if(Dispatcher.UIThread.CheckAccess()) {
				return func();
			}
			return Dispatcher.UIThread.InvokeAsync(func).Result;
		}

		private static void RunOnUiThread(Action action)
		{
			if(Dispatcher.UIThread.CheckAccess()) {
				action();
			} else {
				Dispatcher.UIThread.InvokeAsync(action).Wait();
			}
		}

		// ── Label Commands ───────────────────────────────────────────────────

		private static CodeLabel BuildCodeLabel(JsonNode node)
		{
			uint address = ParseHexOrDec(node["address"]);
			MemoryType memType = ParseMemoryType(node["memoryType"]);
			string label = node["label"]?.GetValue<string>() ?? "";
			string comment = node["comment"]?.GetValue<string>() ?? "";
			uint length = (uint)(node["length"]?.GetValue<int>() ?? 1);
			string? categoryStr = node["category"]?.GetValue<string>();

			FunctionCategory category = FunctionCategory.None;
			if(!string.IsNullOrEmpty(categoryStr)) {
				Enum.TryParse(categoryStr, true, out category);
			}

			return new CodeLabel {
				Address = address,
				MemoryType = memType,
				Label = label,
				Comment = comment,
				Length = length,
				Category = category
			};
		}

		private static JsonObject BuildLabelResponse(CodeLabel cl)
		{
			return new JsonObject {
				["address"] = cl.Address.ToString("X6"),
				["label"] = cl.Label,
				["comment"] = cl.Comment,
				["category"] = cl.Category.ToString()
			};
		}

		private static JsonObject BuildLabelResponseWithWarning(CodeLabel cl)
		{
			var obj = BuildLabelResponse(cl);
			if(cl.Category == FunctionCategory.None) {
				obj["warning"] = "No category set. Setting a category is highly recommended for organization.";
			}
			return obj;
		}

		private static string CmdSetLabel(JsonNode root)
		{
			CodeLabel codeLabel = BuildCodeLabel(root);
			RunOnUiThread(() => {
				LabelManager.SetLabel(codeLabel, true);
				LabelManager.MarkAsIpcModified(codeLabel.Address, codeLabel.MemoryType);
			});
			return Ok(BuildLabelResponseWithWarning(codeLabel));
		}

		private static string CmdSetLabels(JsonNode root)
		{
			JsonArray? labelsArr = root["labels"]?.AsArray();
			if(labelsArr == null || labelsArr.Count == 0) return Error("Missing or empty 'labels' array");

			var results = new JsonArray();
			var codeLabels = new List<CodeLabel>();

			foreach(JsonNode? item in labelsArr) {
				if(item == null) continue;
				try {
					codeLabels.Add(BuildCodeLabel(item));
				} catch(Exception ex) {
					results.Add((JsonNode)new JsonObject {
						["error"] = ex.Message,
						["address"] = item["address"]?.ToString() ?? "?"
					});
				}
			}

			RunOnUiThread(() => {
				foreach(var cl in codeLabels) {
					LabelManager.SetLabel(cl, true);
					LabelManager.MarkAsIpcModified(cl.Address, cl.MemoryType);
				}
			});

			foreach(var cl in codeLabels) {
				results.Add((JsonNode)BuildLabelResponseWithWarning(cl));
			}

			return Ok(new JsonObject {
				["count"] = codeLabels.Count,
				["results"] = results
			});
		}

		private static string CmdDeleteLabel(JsonNode root)
		{
			uint address = ParseHexOrDec(root["address"]);
			MemoryType memType = ParseMemoryType(root["memoryType"]);

			bool deleted = RunOnUiThread(() => {
				CodeLabel? existing = LabelManager.GetLabel(address, memType);
				if(existing != null) {
					LabelManager.DeleteLabel(existing, true);
					return true;
				}
				return false;
			});

			return deleted ? Ok() : Error("Label not found");
		}

		private static string CmdGetLabel(JsonNode root)
		{
			uint address = ParseHexOrDec(root["address"]);
			MemoryType memType = ParseMemoryType(root["memoryType"]);

			CodeLabel? lbl = LabelManager.GetLabel(address, memType);
			if(lbl == null) return Ok();
			return Ok(LabelToJson(lbl));
		}

		private static string CmdGetLabelByName(JsonNode root)
		{
			string? name = root["name"]?.GetValue<string>();
			if(string.IsNullOrEmpty(name)) return Error("Missing 'name'");

			CodeLabel? lbl = LabelManager.GetLabel(name);
			if(lbl == null) return Ok();
			return Ok(LabelToJson(lbl));
		}

		private static string CmdGetAllLabels(JsonNode root)
		{
			CpuType? cpuFilter = null;
			if(root["cpuType"] != null) {
				cpuFilter = ParseCpuType(root["cpuType"]);
			}

			List<CodeLabel> labels = cpuFilter.HasValue
				? LabelManager.GetLabels(cpuFilter.Value)
				: LabelManager.GetAllLabels();

			var arr = new JsonArray();
			foreach(var lbl in labels) {
				arr.Add((JsonNode)LabelToJson(lbl));
			}
			return Ok(arr);
		}

		// ── Memory Commands ──────────────────────────────────────────────────

		private static string CmdReadMemory(JsonNode root)
		{
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			uint start = ParseHexOrDec(root["address"]);
			uint length = (uint)(root["length"]?.GetValue<int>() ?? 1);

			if(length > 0x10000) return Error("Length exceeds maximum (65536 bytes)");

			byte[] data = DebugApi.GetMemoryValues(memType, start, start + length - 1);

			var bytesArr = new JsonArray();
			foreach(byte b in data) bytesArr.Add((JsonNode)(int)b);

			return Ok(new JsonObject {
				["address"] = start.ToString("X6"),
				["length"] = data.Length,
				["hex"] = BitConverter.ToString(data).Replace("-", " "),
				["bytes"] = bytesArr
			});
		}

		private static string CmdWriteMemory(JsonNode root)
		{
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			uint address = ParseHexOrDec(root["address"]);

			// Accept either "values" array or "hex" string
			byte[] data;
			if(root["hex"] != null) {
				string hex = root["hex"]!.GetValue<string>().Replace(" ", "").Replace("-", "");
				data = new byte[hex.Length / 2];
				for(int i = 0; i < data.Length; i++) {
					data[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
				}
			} else if(root["values"] != null) {
				var arr = root["values"]!.AsArray();
				data = arr.Select(v => (byte)v!.GetValue<int>()).ToArray();
			} else {
				return Error("Provide 'hex' string or 'values' byte array");
			}

			DebugApi.SetMemoryValues(memType, address, data, data.Length);
			return Ok(new JsonObject {
				["address"] = address.ToString("X6"),
				["bytesWritten"] = data.Length
			});
		}

		private static string CmdGetMemorySize(JsonNode root)
		{
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			int size = DebugApi.GetMemorySize(memType);
			return Ok(new JsonObject {
				["memoryType"] = memType.ToString(),
				["size"] = size
			});
		}

		// ── CPU State Commands ───────────────────────────────────────────────

		private static string CmdGetCpuState(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);

			if(cpuType == CpuType.Snes) {
				var state = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
				return Ok(BuildSnesCpuStateObject(state));
			}

			if(cpuType == CpuType.Spc) {
				var state = DebugApi.GetCpuState<SpcState>(CpuType.Spc);
				return Ok(BuildSpcCpuStateObject(state));
			}

			uint pc = DebugApi.GetProgramCounter(cpuType, true);
			return Ok(new JsonObject {
				["cpuType"] = cpuType.ToString(),
				["pc"] = pc.ToString("X6")
			});
		}

		private static string CmdSetCpuState(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);

			if(cpuType == CpuType.Snes) {
				var state = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);

				if(root["a"] != null) state.A = (UInt16)ParseHexOrDec(root["a"]);
				if(root["x"] != null) state.X = (UInt16)ParseHexOrDec(root["x"]);
				if(root["y"] != null) state.Y = (UInt16)ParseHexOrDec(root["y"]);
				if(root["sp"] != null) state.SP = (UInt16)ParseHexOrDec(root["sp"]);
				if(root["d"] != null) state.D = (UInt16)ParseHexOrDec(root["d"]);
				if(root["dbr"] != null) state.DBR = (byte)ParseHexOrDec(root["dbr"]);
				if(root["k"] != null) state.K = (byte)ParseHexOrDec(root["k"]);
				if(root["pc"] != null) state.PC = (UInt16)ParseHexOrDec(root["pc"]);
				if(root["emulationMode"] != null) state.EmulationMode = root["emulationMode"]!.GetValue<bool>();

				if(root["flags"] != null) {
					string flagStr = root["flags"]!.GetValue<string>();
					if(Enum.TryParse<SnesCpuFlags>(flagStr, true, out var flags)) {
						state.PS = flags;
					} else if(uint.TryParse(flagStr, NumberStyles.HexNumber, null, out uint rawFlags)) {
						state.PS = (SnesCpuFlags)rawFlags;
					}
				}

				DebugApi.SetCpuState(state, CpuType.Snes);

				return Ok(BuildSnesCpuStateObject(state));
			}

			if(cpuType == CpuType.Spc) {
				var state = DebugApi.GetCpuState<SpcState>(CpuType.Spc);

				if(root["a"] != null) state.A = (byte)ParseHexOrDec(root["a"]);
				if(root["x"] != null) state.X = (byte)ParseHexOrDec(root["x"]);
				if(root["y"] != null) state.Y = (byte)ParseHexOrDec(root["y"]);
				if(root["sp"] != null) state.SP = (byte)ParseHexOrDec(root["sp"]);
				if(root["pc"] != null) state.PC = (UInt16)ParseHexOrDec(root["pc"]);

				if(root["flags"] != null) {
					string flagStr = root["flags"]!.GetValue<string>();
					if(Enum.TryParse<SpcFlags>(flagStr, true, out var flags)) {
						state.PS = flags;
					} else if(uint.TryParse(flagStr, NumberStyles.HexNumber, null, out uint rawFlags)) {
						state.PS = (SpcFlags)(byte)rawFlags;
					}
				}

				DebugApi.SetCpuState(state, CpuType.Spc);

				return Ok(BuildSpcCpuStateObject(state));
			}

			return Error($"setCpuState not yet supported for {cpuType}");
		}

		private static JsonObject BuildSnesCpuStateObject(SnesCpuState state)
		{
			return new JsonObject {
				["cpuType"] = "Snes",
				["a"] = state.A.ToString("X4"),
				["x"] = state.X.ToString("X4"),
				["y"] = state.Y.ToString("X4"),
				["sp"] = state.SP.ToString("X4"),
				["d"] = state.D.ToString("X4"),
				["pc"] = state.PC.ToString("X4"),
				["k"] = state.K.ToString("X2"),
				["dbr"] = state.DBR.ToString("X2"),
				["flags"] = state.PS.ToString(),
				["emulationMode"] = state.EmulationMode,
				["cycleCount"] = (long)state.CycleCount
			};
		}

		private static JsonObject BuildSpcCpuStateObject(SpcState state)
		{
			var cpuRegs = new JsonArray();
			if(state.CpuRegs != null) foreach(byte b in state.CpuRegs) cpuRegs.Add((JsonNode)JsonValue.Create(b.ToString("X2"))!);
			var outRegs = new JsonArray();
			if(state.OutputReg != null) foreach(byte b in state.OutputReg) outRegs.Add((JsonNode)JsonValue.Create(b.ToString("X2"))!);

			return new JsonObject {
				["cpuType"] = "Spc",
				["a"] = state.A.ToString("X2"),
				["x"] = state.X.ToString("X2"),
				["y"] = state.Y.ToString("X2"),
				["sp"] = state.SP.ToString("X2"),
				["pc"] = state.PC.ToString("X4"),
				["flags"] = state.PS.ToString(),
				["cycle"] = (long)state.Cycle,
				["writeEnabled"] = state.WriteEnabled,
				["romEnabled"] = state.RomEnabled,
				["dspReg"] = state.DspReg.ToString("X2"),
				["cpuRegs"] = cpuRegs,
				["outputReg"] = outRegs,
				["timer0Output"] = state.Timer0.Output,
				["timer1Output"] = state.Timer1.Output,
				["timer2Output"] = state.Timer2.Output
			};
		}

		private static string CmdGetProgramCounter(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			uint pc = DebugApi.GetProgramCounter(cpuType, true);
			return Ok(new JsonObject {
				["cpuType"] = cpuType.ToString(),
				["pc"] = pc.ToString("X6")
			});
		}

		private static string CmdSetProgramCounter(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			uint address = ParseHexOrDec(root["address"]);
			DebugApi.SetProgramCounter(cpuType, address);
			return Ok(new JsonObject {
				["cpuType"] = cpuType.ToString(),
				["pc"] = address.ToString("X6")
			});
		}

		// ── Execution Control ────────────────────────────────────────────────

		private static string CmdPause()
		{
			EmuApi.Pause();
			return Ok();
		}

		private static string CmdResume()
		{
			RunOnUiThread(() => {
				DebugApi.ResumeExecution();
				EmuApi.Resume();
			});
			return Ok();
		}

		private static string CmdIsPaused()
		{
			bool paused = EmuApi.IsPaused();
			return Ok(new JsonObject { ["paused"] = paused });
		}

		private static string CmdStep(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			int count = root["count"]?.GetValue<int>() ?? 1;
			StepType stepType = ParseStepType(root["stepType"]);

			DebugApi.Step(cpuType, count, stepType);
			return Ok(new JsonObject {
				["cpuType"] = cpuType.ToString(),
				["count"] = count,
				["stepType"] = stepType.ToString()
			});
		}

		/// <summary>
		/// Step N times and return the CPU state after each step.
		/// For StepBack, count is the number of back-steps, not the StepBackType.
		/// Use stepBackUnit to control granularity (Instruction, Scanline, Frame).
		/// Max 500 steps per call.
		/// </summary>
		private static string CmdStepTrace(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			int count = root["count"]?.GetValue<int>() ?? 1;
			StepType stepType = ParseStepType(root["stepType"]);

			if(count < 1) count = 1;
			if(count > 500) count = 500;

			// For StepBack, stepBackUnit determines granularity.
			int stepBackUnit = 0; // Instruction
			if(stepType == StepType.StepBack) {
				string? unitStr = root["stepBackUnit"]?.GetValue<string>();
				if(!string.IsNullOrEmpty(unitStr)) {
					stepBackUnit = unitStr.ToLowerInvariant() switch {
						"scanline" => 1,
						"frame" => 2,
						_ => 0
					};
				}
			}

			var states = new JsonArray();
			for(int i = 0; i < count; i++) {
				if(stepType == StepType.StepBack) {
					DebugApi.Step(cpuType, stepBackUnit, StepType.StepBack);
				} else {
					DebugApi.Step(cpuType, 1, stepType);
				}

				if(cpuType == CpuType.Snes) {
					var state = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
					states.Add((JsonNode)BuildSnesCpuStateObject(state));
				} else if(cpuType == CpuType.Spc) {
					var state = DebugApi.GetCpuState<SpcState>(CpuType.Spc);
					states.Add((JsonNode)BuildSpcCpuStateObject(state));
				} else {
					uint pc = DebugApi.GetProgramCounter(cpuType, true);
					states.Add((JsonNode)new JsonObject {
						["cpuType"] = cpuType.ToString(),
						["pc"] = pc.ToString("X6")
					});
				}
			}

			return Ok(new JsonObject {
				["cpuType"] = cpuType.ToString(),
				["stepType"] = stepType.ToString(),
				["count"] = states.Count,
				["states"] = states
			});
		}

		private static StepType ParseStepType(JsonNode? node)
		{
			string? typeStr = node?.GetValue<string>();
			StepType stepType = StepType.Step;
			if(!string.IsNullOrEmpty(typeStr)) {
				Enum.TryParse(typeStr, true, out stepType);
			}
			return stepType;
		}

		// ── Disassembly Commands ─────────────────────────────────────────────

		private static string CmdGetDisassembly(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			uint address = ParseHexOrDec(root["address"]);
			uint rows = (uint)(root["rows"]?.GetValue<int>() ?? 20);

			if(rows > 500) rows = 500;

			CodeLineData[] lines = DebugApi.GetDisassemblyOutput(cpuType, address, rows);

			var arr = new JsonArray();
			foreach(var line in lines) {
				var lineObj = new JsonObject {
					["text"] = line.Text,
					["byteCode"] = line.ByteCodeStr,
					["comment"] = line.Comment,
					["flags"] = line.Flags.ToString()
				};
				if(line.Address >= 0) {
					lineObj["address"] = line.Address.ToString("X6");
				}
				if(line.AbsoluteAddress.Address >= 0) {
					lineObj["absAddress"] = new JsonObject {
						["address"] = line.AbsoluteAddress.Address.ToString("X6"),
						["memoryType"] = line.AbsoluteAddress.Type.ToString()
					};
				}
				arr.Add((JsonNode)lineObj);
			}
			return Ok(arr);
		}

		private static string CmdSearchDisassembly(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			string? searchStr = root["search"]?.GetValue<string>();
			if(string.IsNullOrEmpty(searchStr)) return Error("Missing 'search'");

			int startAddr = root["startAddress"] != null ? (int)ParseHexOrDec(root["startAddress"]) : 0;

			int result = DebugApi.SearchDisassembly(cpuType, searchStr, startAddr, new DisassemblySearchOptions());
			var obj = new JsonObject { ["found"] = result >= 0 };
			if(result >= 0) obj["address"] = result.ToString("X6");
			return Ok(obj);
		}

		// ── Breakpoint Commands ──────────────────────────────────────────────

		private static string CmdAddBreakpoint(JsonNode root)
		{
			uint startAddr = ParseHexOrDec(root["address"]);
			uint endAddr = root["endAddress"] != null ? ParseHexOrDec(root["endAddress"]) : startAddr;
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			CpuType cpuType = ParseCpuType(root["cpuType"]);

			bool onRead = root["breakOnRead"]?.GetValue<bool>() ?? false;
			bool onWrite = root["breakOnWrite"]?.GetValue<bool>() ?? false;
			bool onExec = root["breakOnExec"]?.GetValue<bool>() ?? true;
			string condition = root["condition"]?.GetValue<string>() ?? "";
			bool enabled = root["enabled"]?.GetValue<bool>() ?? true;

			RunOnUiThread(() => {
				var bp = new Breakpoint {
					StartAddress = startAddr,
					EndAddress = endAddr,
					MemoryType = memType,
					CpuType = cpuType,
					BreakOnRead = onRead,
					BreakOnWrite = onWrite,
					BreakOnExec = onExec,
					Condition = condition,
					Enabled = enabled
				};
				BreakpointManager.AddBreakpoint(bp);
				BreakpointManager.MarkAsIpcSet(bp);
			});

			return Ok(new JsonObject {
				["address"] = startAddr.ToString("X6"),
				["endAddress"] = endAddr.ToString("X6"),
				["memoryType"] = memType.ToString()
			});
		}

		private static string CmdRemoveBreakpoint(JsonNode root)
		{
			uint address = ParseHexOrDec(root["address"]);
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			CpuType cpuType = ParseCpuType(root["cpuType"]);

			bool removed = RunOnUiThread(() => {
				var info = new AddressInfo { Address = (int)address, Type = memType };
				var bp = BreakpointManager.GetMatchingBreakpoint(info, cpuType);
				if(bp != null) {
					BreakpointManager.RemoveBreakpoint(bp);
					return true;
				}
				return false;
			});

			return removed ? Ok() : Error("No breakpoint found at that address");
		}

		private static string CmdGetBreakpoints(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			var bps = BreakpointManager.GetBreakpoints(cpuType);

			var arr = new JsonArray();
			foreach(var bp in bps) {
				arr.Add((JsonNode)new JsonObject {
					["startAddress"] = bp.StartAddress.ToString("X6"),
					["endAddress"] = bp.EndAddress.ToString("X6"),
					["memoryType"] = bp.MemoryType.ToString(),
					["cpuType"] = bp.CpuType.ToString(),
					["breakOnRead"] = bp.BreakOnRead,
					["breakOnWrite"] = bp.BreakOnWrite,
					["breakOnExec"] = bp.BreakOnExec,
					["condition"] = bp.Condition,
					["enabled"] = bp.Enabled
				});
			}
			return Ok(arr);
		}

		private static string CmdClearBreakpoints()
		{
			RunOnUiThread(() => BreakpointManager.ClearBreakpoints());
			return Ok();
		}

		// ── Expression Evaluation ────────────────────────────────────────────

		private static string CmdEvaluate(JsonNode root)
		{
			string? expression = root["expression"]?.GetValue<string>();
			if(string.IsNullOrEmpty(expression)) return Error("Missing 'expression'");

			CpuType cpuType = ParseCpuType(root["cpuType"]);

			long value = DebugApi.EvaluateExpression(expression, cpuType, out EvalResultType resultType, false);

			return Ok(new JsonObject {
				["expression"] = expression,
				["value"] = value,
				["hex"] = value.ToString("X"),
				["resultType"] = resultType.ToString()
			});
		}

		// ── Call Stack ───────────────────────────────────────────────────────

		private static string CmdGetCallstack(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			StackFrameInfo[] frames = DebugApi.GetCallstack(cpuType);

			var arr = new JsonArray();
			foreach(var f in frames) {
				arr.Add((JsonNode)new JsonObject {
					["source"] = f.Source.ToString("X6"),
					["target"] = f.Target.ToString("X6"),
					["returnAddress"] = f.Return.ToString("X6"),
					["flags"] = f.Flags.ToString()
				});
			}
			return Ok(arr);
		}

		// ── CDL Commands ─────────────────────────────────────────────────────

		private static string CmdGetCdlData(JsonNode root)
		{
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			uint offset = ParseHexOrDec(root["address"]);
			uint length = (uint)(root["length"]?.GetValue<int>() ?? 1);

			if(length > 0x10000) return Error("Length exceeds maximum (65536 bytes)");

			CdlFlags[] data = DebugApi.GetCdlData(offset, length, memType);

			var arr = new JsonArray();
			for(int i = 0; i < data.Length; i++) {
				arr.Add((JsonNode)new JsonObject {
					["address"] = (offset + i).ToString("X6"),
					["flags"] = data[i].ToString()
				});
			}
			return Ok(arr);
		}

		private static string CmdGetCdlStatistics(JsonNode root)
		{
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			CdlStatistics stats = DebugApi.GetCdlStatistics(memType);
			return Ok(new JsonObject {
				["codeBytes"] = stats.CodeBytes,
				["dataBytes"] = stats.DataBytes,
				["totalBytes"] = stats.TotalBytes
			});
		}

		private static string CmdGetCdlFunctions(JsonNode root)
		{
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			uint[] functions = DebugApi.GetCdlFunctions(memType);

			var arr = new JsonArray();
			foreach(uint a in functions) arr.Add((JsonNode)a.ToString("X6"));
			return Ok(arr);
		}

		// ── Address Mapping ──────────────────────────────────────────────────

		private static string CmdGetAbsoluteAddress(JsonNode root)
		{
			uint address = ParseHexOrDec(root["address"]);
			MemoryType memType = ParseMemoryType(root["memoryType"]);

			AddressInfo rel = new AddressInfo { Address = (int)address, Type = memType };
			AddressInfo abs = DebugApi.GetAbsoluteAddress(rel);

			if(abs.Address < 0) return Ok();
			return Ok(new JsonObject {
				["address"] = abs.Address.ToString("X6"),
				["memoryType"] = abs.Type.ToString()
			});
		}

		private static string CmdGetRelativeAddress(JsonNode root)
		{
			uint address = ParseHexOrDec(root["address"]);
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			CpuType cpuType = ParseCpuType(root["cpuType"]);

			AddressInfo abs = new AddressInfo { Address = (int)address, Type = memType };
			AddressInfo rel = DebugApi.GetRelativeAddress(abs, cpuType);

			if(rel.Address < 0) return Ok();
			return Ok(new JsonObject {
				["address"] = rel.Address.ToString("X6"),
				["memoryType"] = rel.Type.ToString()
			});
		}

		// ── ROM Info & Status ────────────────────────────────────────────────

		private static string CmdGetRomInfo()
		{
			RomInfo info = EmuApi.GetRomInfo();
			var cpuArr = new JsonArray();
			foreach(var c in info.CpuTypes) cpuArr.Add((JsonNode)c.ToString());

			return Ok(new JsonObject {
				["romPath"] = info.RomPath,
				["format"] = info.Format.ToString(),
				["consoleType"] = info.ConsoleType.ToString(),
				["cpuTypes"] = cpuArr
			});
		}

		private static string CmdGetStatus()
		{
			bool running = EmuApi.IsRunning();
			bool paused = EmuApi.IsPaused();
			RomInfo info = EmuApi.GetRomInfo();

			JsonObject? cpuState = null;
			if(running) {
				try {
					uint pc = DebugApi.GetProgramCounter(CpuType.Snes, true);
					cpuState = new JsonObject { ["pc"] = pc.ToString("X6") };
				} catch { }
			}

			var obj = new JsonObject {
				["running"] = running,
				["paused"] = paused,
				["romLoaded"] = running,
				["romPath"] = info.RomPath,
				["consoleType"] = info.ConsoleType.ToString()
			};
			if(cpuState != null) obj["cpuState"] = cpuState;
			return Ok(obj);
		}

		// ── Screenshot ───────────────────────────────────────────────────────

		private static string CmdTakeScreenshot(JsonNode root)
		{
			string? path = root["path"]?.GetValue<string>();
			if(!string.IsNullOrEmpty(path)) {
				EmuApi.TakeScreenshotToFile(path);
			} else {
				EmuApi.TakeScreenshot();
			}
			var obj = new JsonObject();
			if(path != null) obj["path"] = path;
			return Ok(obj);
		}

		// ── Emulator Control ─────────────────────────────────────────────────

		private static string CmdLoadRom(JsonNode root)
		{
			string? path = root["path"]?.GetValue<string>();
			if(string.IsNullOrEmpty(path)) return Error("Missing 'path'");
			if(!File.Exists(path)) return Error($"ROM file not found: {path}");

			string? patchPath = root["patchPath"]?.GetValue<string>();
			if(!string.IsNullOrEmpty(patchPath) && !File.Exists(patchPath)) {
				return Error($"Patch file not found: {patchPath}");
			}

			// LoadRomHelper dispatches to a background task internally.
			RunOnUiThread(() => {
				if(string.IsNullOrEmpty(patchPath)) {
					LoadRomHelper.LoadRom(path);
				} else {
					LoadRomHelper.LoadRom(path, patchPath);
				}
			});

			return Ok(new JsonObject {
				["path"] = path,
				["patchPath"] = patchPath
			});
		}

		private static string CmdReloadRom()
		{
			if(!EmuApi.IsRunning()) return Error("No ROM is currently loaded");
			LoadRomHelper.ReloadRom();
			return Ok();
		}

		private static string CmdPowerCycle()
		{
			if(!EmuApi.IsRunning()) return Error("No ROM is currently loaded");
			LoadRomHelper.PowerCycle();
			return Ok();
		}

		private static string CmdPowerOff()
		{
			if(!EmuApi.IsRunning()) return Error("No ROM is currently loaded");
			LoadRomHelper.PowerOff();
			return Ok();
		}

		private static string CmdReset()
		{
			if(!EmuApi.IsRunning()) return Error("No ROM is currently loaded");
			LoadRomHelper.Reset();
			return Ok();
		}

		// ── Save States ──────────────────────────────────────────────────────

		private static uint ParseSlot(JsonNode? node)
		{
			if(node == null) throw new ArgumentException("Missing 'slot' (1-10)");
			int slot = node.GetValue<int>();
			if(slot < 1 || slot > 10) throw new ArgumentException("Slot must be between 1 and 10");
			return (uint)slot;
		}

		private static string CmdSaveStateSlot(JsonNode root)
		{
			if(!EmuApi.IsRunning()) return Error("No ROM is currently loaded");
			uint slot = ParseSlot(root["slot"]);
			EmuApi.SaveState(slot);
			return Ok(new JsonObject { ["slot"] = (int)slot });
		}

		private static string CmdLoadStateSlot(JsonNode root)
		{
			if(!EmuApi.IsRunning()) return Error("No ROM is currently loaded");
			uint slot = ParseSlot(root["slot"]);
			EmuApi.LoadState(slot);
			return Ok(new JsonObject { ["slot"] = (int)slot });
		}

		private static string CmdSaveStateFile(JsonNode root)
		{
			if(!EmuApi.IsRunning()) return Error("No ROM is currently loaded");
			string? path = root["path"]?.GetValue<string>();
			if(string.IsNullOrEmpty(path)) return Error("Missing 'path'");
			EmuApi.SaveStateFile(path);
			return Ok(new JsonObject { ["path"] = path });
		}

		private static string CmdLoadStateFile(JsonNode root)
		{
			if(!EmuApi.IsRunning()) return Error("No ROM is currently loaded");
			string? path = root["path"]?.GetValue<string>();
			if(string.IsNullOrEmpty(path)) return Error("Missing 'path'");
			if(!File.Exists(path)) return Error($"Save state file not found: {path}");
			EmuApi.LoadStateFile(path);
			return Ok(new JsonObject { ["path"] = path });
		}

		// ── Controller Input Override ────────────────────────────────────────

		private static bool GetBtn(JsonNode root, string name)
		{
			JsonNode? n = root[name];
			if(n == null) return false;
			try { return n.GetValue<bool>(); } catch { }
			try { return n.GetValue<int>() != 0; } catch { }
			return false;
		}

		private static string CmdSetControllerInput(JsonNode root)
		{
			int port = root["port"]?.GetValue<int>() ?? 0;
			if(port < 0 || port > 7) return Error("Port must be between 0 and 7");

			// Accept either a "buttons" sub-object or flat keys on the root.
			JsonNode src = root["buttons"] ?? root;

			DebugControllerState state = new DebugControllerState {
				A      = GetBtn(src, "a"),
				B      = GetBtn(src, "b"),
				X      = GetBtn(src, "x"),
				Y      = GetBtn(src, "y"),
				L      = GetBtn(src, "l"),
				R      = GetBtn(src, "r"),
				U      = GetBtn(src, "u"),
				D      = GetBtn(src, "d"),
				Up     = GetBtn(src, "up"),
				Down   = GetBtn(src, "down"),
				Left   = GetBtn(src, "left"),
				Right  = GetBtn(src, "right"),
				Select = GetBtn(src, "select"),
				Start  = GetBtn(src, "start")
			};

			DebugApi.SetInputOverrides((uint)port, state);

			return Ok(new JsonObject {
				["port"] = port,
				["state"] = new JsonObject {
					["a"] = state.A, ["b"] = state.B, ["x"] = state.X, ["y"] = state.Y,
					["l"] = state.L, ["r"] = state.R, ["u"] = state.U, ["d"] = state.D,
					["up"] = state.Up, ["down"] = state.Down,
					["left"] = state.Left, ["right"] = state.Right,
					["select"] = state.Select, ["start"] = state.Start
				}
			});
		}

		private static string CmdClearControllerInput(JsonNode root)
		{
			int port = root["port"]?.GetValue<int>() ?? 0;
			if(port < 0 || port > 7) return Error("Port must be between 0 and 7");

			DebugApi.SetInputOverrides((uint)port, new DebugControllerState());
			return Ok(new JsonObject { ["port"] = port });
		}

		// ── Emulation Settings ───────────────────────────────────────────────

		private static string CmdGetEmulationSpeed()
		{
			return Ok(new JsonObject { ["speed"] = (int)ConfigManager.Config.Emulation.EmulationSpeed });
		}

		private static string CmdSetEmulationSpeed(JsonNode root)
		{
			int speed = root["speed"]?.GetValue<int>() ?? -1;
			if(speed < 0 || speed > 5000) return Error("Speed must be 0-5000 (0=unlimited)");
			ConfigManager.Config.Emulation.EmulationSpeed = (uint)speed;
			ConfigManager.Config.Emulation.ApplyConfig();
			return Ok(new JsonObject { ["speed"] = speed });
		}

		private static string CmdGetTurboSpeed()
		{
			return Ok(new JsonObject { ["speed"] = (int)ConfigManager.Config.Emulation.TurboSpeed });
		}

		private static string CmdSetTurboSpeed(JsonNode root)
		{
			int speed = root["speed"]?.GetValue<int>() ?? -1;
			if(speed < 0 || speed > 5000) return Error("Speed must be 0-5000 (0=unlimited)");
			ConfigManager.Config.Emulation.TurboSpeed = (uint)speed;
			ConfigManager.Config.Emulation.ApplyConfig();
			return Ok(new JsonObject { ["speed"] = speed });
		}

		private static string CmdGetRunAheadFrames()
		{
			return Ok(new JsonObject { ["frames"] = (int)ConfigManager.Config.Emulation.RunAheadFrames });
		}

		private static string CmdSetRunAheadFrames(JsonNode root)
		{
			int frames = root["frames"]?.GetValue<int>() ?? -1;
			if(frames < 0 || frames > 10) return Error("Frames must be 0-10");
			ConfigManager.Config.Emulation.RunAheadFrames = (uint)frames;
			ConfigManager.Config.Emulation.ApplyConfig();
			return Ok(new JsonObject { ["frames"] = frames });
		}

		private static string CmdGetConfig()
		{
			var emu = ConfigManager.Config.Emulation;
			return Ok(new JsonObject {
				["emulationSpeed"] = (int)emu.EmulationSpeed,
				["turboSpeed"] = (int)emu.TurboSpeed,
				["rewindSpeed"] = (int)emu.RewindSpeed,
				["runAheadFrames"] = (int)emu.RunAheadFrames
			});
		}

		// ── Timing & PPU ─────────────────────────────────────────────────────

		private static string CmdGetTimingInfo(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);
			TimingInfo timing = EmuApi.GetTimingInfo(cpuType);
			return Ok(new JsonObject {
				["fps"] = timing.Fps,
				["masterClock"] = (long)timing.MasterClock,
				["masterClockRate"] = (long)timing.MasterClockRate,
				["frameCount"] = (long)timing.FrameCount,
				["scanlineCount"] = (long)timing.ScanlineCount,
				["firstScanline"] = timing.FirstScanline,
				["cycleCount"] = (long)timing.CycleCount
			});
		}

		private static string CmdGetPpuState(JsonNode root)
		{
			CpuType cpuType = ParseCpuType(root["cpuType"]);

			if(cpuType == CpuType.Snes) {
				var ppu = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);
				return Ok(new JsonObject {
					["cpuType"] = "Snes",
					["cycle"] = ppu.Cycle,
					["scanline"] = ppu.Scanline,
					["hClock"] = ppu.HClock,
					["frameCount"] = (long)ppu.FrameCount,
					["forcedBlank"] = ppu.ForcedBlank,
					["screenBrightness"] = ppu.ScreenBrightness,
					["bgMode"] = ppu.BgMode,
					["mode1Bg3Priority"] = ppu.Mode1Bg3Priority,
					["mainScreenLayers"] = ppu.MainScreenLayers,
					["subScreenLayers"] = ppu.SubScreenLayers,
					["vramAddress"] = ppu.VramAddress.ToString("X4")
				});
			}

			return Error($"PPU state not supported for {cpuType}");
		}

		// ── SPC700 / DSP state ───────────────────────────────────────────────

		private static string CmdGetSpcState(JsonNode root)
		{
			var state = DebugApi.GetCpuState<SpcState>(CpuType.Spc);
			return Ok(BuildSpcCpuStateObject(state));
		}

		private static string CmdGetDspState(JsonNode root)
		{
			var snes = DebugApi.GetConsoleState<SnesState>(ConsoleType.Snes);
			var dsp = snes.Dsp;
			bool decodeVoices = root["decodeVoices"]?.GetValue<bool>() ?? true;

			var ext = new JsonArray();
			if(dsp.ExternalRegs != null) foreach(byte b in dsp.ExternalRegs) ext.Add((JsonNode)JsonValue.Create(b.ToString("X2"))!);
			var inner = new JsonArray();
			if(dsp.Regs != null) foreach(byte b in dsp.Regs) inner.Add((JsonNode)JsonValue.Create(b.ToString("X2"))!);

			var obj = new JsonObject {
				["externalRegs"] = ext,
				["regs"] = inner,
				["noiseLfsr"] = dsp.NoiseLfsr,
				["counter"] = dsp.Counter,
				["step"] = dsp.Step,
				["voiceOutput"] = dsp.VoiceOutput,
				["pitch"] = dsp.Pitch,
				["sampleAddress"] = dsp.SampleAddress.ToString("X4"),
				["brrNextAddress"] = dsp.BrrNextAddress.ToString("X4"),
				["dirSampleTableAddress"] = dsp.DirSampleTableAddress.ToString("X2"),
				["noiseOn"] = dsp.NoiseOn.ToString("X2"),
				["pitchModulationOn"] = dsp.PitchModulationOn.ToString("X2"),
				["keyOn"] = dsp.KeyOn.ToString("X2"),
				["newKeyOn"] = dsp.NewKeyOn.ToString("X2"),
				["keyOff"] = dsp.KeyOff.ToString("X2"),
				["sourceNumber"] = dsp.SourceNumber.ToString("X2"),
				["brrHeader"] = dsp.BrrHeader.ToString("X2"),
				["voiceEndBuffer"] = dsp.VoiceEndBuffer.ToString("X2")
			};

			if(decodeVoices && dsp.ExternalRegs != null && dsp.ExternalRegs.Length >= 128) {
				var voices = new JsonArray();
				byte[] r = dsp.ExternalRegs;
				for(int v = 0; v < 8; v++) {
					int b = v << 4;
					int pitch = r[b + 0x02] | (r[b + 0x03] << 8);
					voices.Add((JsonNode)new JsonObject {
						["index"] = v,
						["volL"] = (sbyte)r[b + 0x00],
						["volR"] = (sbyte)r[b + 0x01],
						["pitch"] = pitch,
						["srcn"] = r[b + 0x04],
						["adsr1"] = r[b + 0x05].ToString("X2"),
						["adsr2"] = r[b + 0x06].ToString("X2"),
						["gain"] = r[b + 0x07].ToString("X2"),
						["envx"] = r[b + 0x08],
						["outx"] = (sbyte)r[b + 0x09],
						["keyOn"] = ((r[0x4C] >> v) & 1) != 0,
						["keyOff"] = ((r[0x5C] >> v) & 1) != 0,
						["voiceEnd"] = ((r[0x7C] >> v) & 1) != 0,
						["pmon"] = v > 0 && ((r[0x2D] >> v) & 1) != 0,
						["non"] = ((r[0x3D] >> v) & 1) != 0,
						["eon"] = ((r[0x4D] >> v) & 1) != 0
					});
				}
				obj["voices"] = voices;
				obj["mainVolL"] = (sbyte)r[0x0C];
				obj["mainVolR"] = (sbyte)r[0x1C];
				obj["echoVolL"] = (sbyte)r[0x2C];
				obj["echoVolR"] = (sbyte)r[0x3C];
				obj["flg"] = r[0x6C].ToString("X2");
				obj["softReset"] = (r[0x6C] & 0x80) != 0;
				obj["muteAmp"] = (r[0x6C] & 0x40) != 0;
				obj["echoDisabled"] = (r[0x6C] & 0x20) != 0;
				obj["noiseClock"] = r[0x6C] & 0x1F;
				obj["dir"] = r[0x5D].ToString("X2");
				obj["esa"] = r[0x6D].ToString("X2");
				obj["edl"] = r[0x7D] & 0x0F;
				obj["efb"] = (sbyte)r[0x0D];
			}

			return Ok(obj);
		}

		// ── IPC Info ─────────────────────────────────────────────────────────

		private static string CmdGetIpcInfo()
		{
			string pipeName = IpcServer.CurrentPipeName;
			string romPath = "";
			try { romPath = EmuApi.GetRomInfo().RomPath; } catch { }

			return Ok(new JsonObject {
				["pipeName"] = pipeName,
				["pipePath"] = IpcServer.GetPlatformPipePath(pipeName),
				["romPath"] = romPath,
				["platform"] = OperatingSystem.IsWindows() ? "windows" : "linux"
			});
		}

		// ── Cheats ───────────────────────────────────────────────────────────

		private static string CmdSetCheats(JsonNode root)
		{
			var cheatsNode = root["cheats"]?.AsArray();
			if(cheatsNode == null || cheatsNode.Count == 0) return Error("Missing 'cheats' array");

			var cheats = new List<InteropCheatCode>();
			foreach(var c in cheatsNode) {
				if(c == null) continue;
				string? typeStr = c["type"]?.GetValue<string>();
				string? code = c["code"]?.GetValue<string>();
				if(string.IsNullOrEmpty(typeStr) || string.IsNullOrEmpty(code)) {
					return Error("Each cheat needs 'type' and 'code'");
				}
				if(!Enum.TryParse<CheatType>(typeStr, true, out var cheatType)) {
					return Error($"Invalid cheat type: {typeStr}");
				}
				cheats.Add(new InteropCheatCode(cheatType, code));
			}

			EmuApi.SetCheats(cheats.ToArray(), (uint)cheats.Count);
			return Ok(new JsonObject { ["count"] = cheats.Count });
		}

		private static string CmdClearCheats()
		{
			EmuApi.ClearCheats();
			return Ok();
		}

		// ── PPU Memory ───────────────────────────────────────────────────────

		private static string CmdGetVram(JsonNode root)
		{
			uint wordAddr = ParseHexOrDec(root["address"] ?? (JsonNode)0);
			uint wordCount = (uint)(root["length"]?.GetValue<int>() ?? 0x8000);

			if(wordCount > 0x8000) return Error("Length exceeds maximum (32768 words = 64KB)");
			if(wordAddr + wordCount > 0x8000) return Error("Address + length exceeds VRAM size (32768 words)");

			uint byteStart = wordAddr * 2;
			uint byteLen = wordCount * 2;
			byte[] data = DebugApi.GetMemoryValues(MemoryType.SnesVideoRam, byteStart, byteStart + byteLen - 1);

			return Ok(new JsonObject {
				["wordAddress"] = wordAddr.ToString("X4"),
				["wordCount"] = (int)wordCount,
				["hex"] = BitConverter.ToString(data).Replace("-", " "),
				["bytes"] = BytesToJsonArray(data)
			});
		}

		private static string CmdGetCgram(JsonNode root)
		{
			uint start = ParseHexOrDec(root["address"] ?? (JsonNode)0);
			uint length = (uint)(root["length"]?.GetValue<int>() ?? 512);

			if(length > 512) return Error("Length exceeds CGRAM size (512 bytes)");
			if(start + length > 512) return Error("Address + length exceeds CGRAM size (512 bytes)");

			byte[] data = DebugApi.GetMemoryValues(MemoryType.SnesCgRam, start, start + length - 1);

			var colors = new JsonArray();
			for(int i = 0; i + 1 < data.Length; i += 2) {
				ushort rgb555 = (ushort)(data[i] | (data[i + 1] << 8));
				int r = (rgb555 & 0x1F) << 3;
				int g = ((rgb555 >> 5) & 0x1F) << 3;
				int b = ((rgb555 >> 10) & 0x1F) << 3;
				colors.Add((JsonNode)new JsonObject {
					["index"] = (start + i) / 2,
					["rgb555"] = rgb555.ToString("X4"),
					["r"] = r, ["g"] = g, ["b"] = b
				});
			}

			return Ok(new JsonObject {
				["address"] = start.ToString("X2"),
				["length"] = data.Length,
				["hex"] = BitConverter.ToString(data).Replace("-", " "),
				["bytes"] = BytesToJsonArray(data),
				["colors"] = colors
			});
		}

		private static string CmdGetOam()
		{
			// OAM = 544 bytes: 512 low table (128 sprites × 4 bytes) + 32 high table
			byte[] data = DebugApi.GetMemoryValues(MemoryType.SnesSpriteRam, 0, 543);

			// Get PPU state for OamMode (sprite size table selection)
			var ppu = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);

			// OAM size table: [OamMode][largeSprite] → {widthTiles, heightTiles}
			int[][,] oamSizes = {
				new int[,] { {1,1}, {2,2} }, // 0: 8x8 + 16x16
				new int[,] { {1,1}, {4,4} }, // 1: 8x8 + 32x32
				new int[,] { {1,1}, {8,8} }, // 2: 8x8 + 64x64
				new int[,] { {2,2}, {4,4} }, // 3: 16x16 + 32x32
				new int[,] { {2,2}, {8,8} }, // 4: 16x16 + 64x64
				new int[,] { {4,4}, {8,8} }, // 5: 32x32 + 64x64
				new int[,] { {2,4}, {4,8} }, // 6: 16x32 + 32x64
				new int[,] { {2,4}, {4,4} }  // 7: 16x32 + 32x32
			};

			var sprites = new JsonArray();
			for(int i = 0; i < 128; i++) {
				int addr = i * 4;
				byte xLow = data[addr];
				byte y = data[addr + 1];
				ushort tile = data[addr + 2];
				byte flags = data[addr + 3];

				// High table: 2 bits per sprite (x bit 8, size toggle)
				int highTableOffset = addr >> 4;
				int shift = ((addr >> 2) & 0x03) << 1;
				byte highBits = (byte)(data[0x200 | highTableOffset] >> shift);

				bool largeSprite = (highBits & 0x02) != 0;
				int xSign = (highBits & 0x01) << 8;
				int x = (short)((ushort)((xSign | xLow) << 7)) >> 7;

				int mode = Math.Min(ppu.OamMode, (byte)7);
				int sizeIdx = largeSprite ? 1 : 0;
				int widthPx = oamSizes[mode][sizeIdx, 0] * 8;
				int heightPx = oamSizes[mode][sizeIdx, 1] * 8;

				// flags byte: vhoopppt tttttttt (t in byte2, flags in byte3)
				// byte3: vhoo pppt → v=vflip, h=hflip, oo=priority, ppp=palette, t=tile bit 8
				tile |= (ushort)((flags & 0x01) << 8);
				int palette = (flags >> 1) & 0x07;
				int priority = (flags >> 4) & 0x03;
				bool hFlip = (flags & 0x40) != 0;
				bool vFlip = (flags & 0x80) != 0;

				sprites.Add((JsonNode)new JsonObject {
					["index"] = i,
					["x"] = x, ["y"] = (int)y,
					["tile"] = tile.ToString("X3"),
					["palette"] = palette,
					["priority"] = priority,
					["hFlip"] = hFlip, ["vFlip"] = vFlip,
					["size"] = largeSprite ? "large" : "small",
					["width"] = widthPx, ["height"] = heightPx
				});
			}

			return Ok(new JsonObject {
				["raw"] = BytesToJsonArray(data),
				["hex"] = BitConverter.ToString(data).Replace("-", " "),
				["oamMode"] = (int)ppu.OamMode,
				["sprites"] = sprites
			});
		}

		private static JsonArray BytesToJsonArray(byte[] data)
		{
			var arr = new JsonArray();
			foreach(byte b in data) arr.Add((JsonNode)(int)b);
			return arr;
		}

		// ── PPU / DMA State ──────────────────────────────────────────────────

		private static readonly int[] _bgBpp = { 0, 2, 4, 8, 8, 4, 2, 0 }; // per BG mode, BG1 default bpp
		private static readonly int[][] _modeBpp = {
			new[] {2,2,2,2}, // mode 0
			new[] {4,2,0,0}, // mode 1
			new[] {4,4,0,0}, // mode 2
			new[] {8,4,0,0}, // mode 3
			new[] {8,2,0,0}, // mode 4
			new[] {4,2,0,0}, // mode 5
			new[] {4,0,0,0}, // mode 6
			new[] {8,0,0,0}  // mode 7
		};

		private static string CmdGetBgState(JsonNode root)
		{
			int? layerParam = root["layer"]?.GetValue<int>();
			var ppu = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);

			int bgMode = ppu.BgMode & 0x07;

			if(layerParam != null) {
				int layer = layerParam.Value;
				if(layer < 1 || layer > 4) return Error("Layer must be 1-4");
				return Ok(BuildBgLayerJson(ppu, bgMode, layer - 1));
			}

			// Return all 4 layers
			var layers = new JsonArray();
			for(int i = 0; i < 4; i++) {
				layers.Add((JsonNode)BuildBgLayerJson(ppu, bgMode, i));
			}
			return Ok(new JsonObject {
				["bgMode"] = bgMode,
				["mode1Bg3Priority"] = ppu.Mode1Bg3Priority,
				["mainScreenLayers"] = ppu.MainScreenLayers,
				["subScreenLayers"] = ppu.SubScreenLayers,
				["layers"] = layers
			});
		}

		private static JsonObject BuildBgLayerJson(SnesPpuState ppu, int bgMode, int layerIndex)
		{
			var layer = ppu.Layers[layerIndex];
			int bpp = (bgMode < _modeBpp.Length && layerIndex < _modeBpp[bgMode].Length)
				? _modeBpp[bgMode][layerIndex] : 0;
			bool enabled = bpp > 0;

			string mapSize;
			if(layer.DoubleWidth && layer.DoubleHeight) mapSize = "64x64";
			else if(layer.DoubleWidth) mapSize = "64x32";
			else if(layer.DoubleHeight) mapSize = "32x64";
			else mapSize = "32x32";

			return new JsonObject {
				["layer"] = layerIndex + 1,
				["enabled"] = enabled,
				["bpp"] = bpp,
				["charBaseAddress"] = layer.ChrAddress.ToString("X4"),
				["mapBaseAddress"] = layer.TilemapAddress.ToString("X4"),
				["mapSize"] = mapSize,
				["tileSize"] = layer.LargeTiles ? "16x16" : "8x8",
				["hScroll"] = layer.HScroll,
				["vScroll"] = layer.VScroll,
				["mainScreen"] = (ppu.MainScreenLayers & (1 << layerIndex)) != 0,
				["subScreen"] = (ppu.SubScreenLayers & (1 << layerIndex)) != 0
			};
		}

		private static string CmdGetDmaState(JsonNode root)
		{
			int? channelParam = root["channel"]?.GetValue<int>();
			var state = DebugApi.GetConsoleState<SnesState>(ConsoleType.Snes);

			if(channelParam != null) {
				int ch = channelParam.Value;
				if(ch < 0 || ch > 7) return Error("Channel must be 0-7");
				return Ok(BuildDmaChannelJson(state.Dma, ch));
			}

			// Return all 8 channels
			var channels = new JsonArray();
			for(int i = 0; i < 8; i++) {
				channels.Add((JsonNode)BuildDmaChannelJson(state.Dma, i));
			}
			return Ok(new JsonObject {
				["hdmaChannels"] = state.Dma.HdmaChannels,
				["channels"] = channels
			});
		}

		private static JsonObject BuildDmaChannelJson(SnesDmaControllerState dma, int ch)
		{
			var c = dma.Channels[ch];
			bool hdmaEnabled = (dma.HdmaChannels & (1 << ch)) != 0;

			return new JsonObject {
				["channel"] = ch,
				["active"] = c.DmaActive,
				["hdmaEnabled"] = hdmaEnabled,
				["transferMode"] = (int)c.TransferMode,
				["direction"] = c.InvertDirection ? "ioToCpu" : "cpuToIo",
				["fixedTransfer"] = c.FixedTransfer,
				["decrement"] = c.Decrement,
				["ioAddress"] = (0x2100 + c.DestAddress).ToString("X4"),
				["sourceBank"] = c.SrcBank.ToString("X2"),
				["sourceAddress"] = c.SrcAddress.ToString("X4"),
				["transferSize"] = (int)c.TransferSize,
				["hdmaIndirect"] = c.HdmaIndirectAddressing,
				["hdmaBank"] = c.HdmaBank.ToString("X2"),
				["hdmaTableAddress"] = c.HdmaTableAddress.ToString("X4"),
				["hdmaLineCounter"] = (int)c.HdmaLineCounterAndRepeat
			};
		}

		// ── Tilemap / Tile Decode ────────────────────────────────────────────

		private static string CmdGetTilemap(JsonNode root)
		{
			int? layerParam = root["layer"]?.GetValue<int>();
			if(layerParam == null || layerParam < 1 || layerParam > 4) return Error("Missing/invalid 'layer' (1-4)");
			int layerIdx = layerParam.Value - 1;

			var ppu = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);
			var layer = ppu.Layers[layerIdx];
			int bgMode = ppu.BgMode & 0x07;
			int bpp = (bgMode < _modeBpp.Length) ? _modeBpp[bgMode][layerIdx] : 0;

			int mapW = layer.DoubleWidth ? 64 : 32;
			int mapH = layer.DoubleHeight ? 64 : 32;

			int startX = root["startX"]?.GetValue<int>() ?? 0;
			int startY = root["startY"]?.GetValue<int>() ?? 0;
			int width = root["width"]?.GetValue<int>() ?? (mapW - startX);
			int height = root["height"]?.GetValue<int>() ?? (mapH - startY);

			if(startX < 0 || startY < 0 || startX >= mapW || startY >= mapH) return Error($"start out of range (map {mapW}x{mapH})");
			if(width <= 0 || height <= 0) return Error("width/height must be > 0");
			if(startX + width > mapW) width = mapW - startX;
			if(startY + height > mapH) height = mapH - startY;
			if(width * height > 8192) return Error("Requested region too large (max 8192 entries)");

			// Fetch entire VRAM once — tilemap could span quadrants up to +0xC00 words from base.
			byte[] vram = DebugApi.GetMemoryValues(MemoryType.SnesVideoRam, 0, 0xFFFF);

			var entries = new JsonArray();
			for(int y = startY; y < startY + height; y++) {
				int vOff = layer.DoubleHeight ? ((y & 0x20) << (layer.DoubleWidth ? 6 : 5)) : 0;
				int baseWord = layer.TilemapAddress + vOff + ((y & 0x1F) << 5);
				for(int x = startX; x < startX + width; x++) {
					int word = baseWord + (x & 0x1F) + (layer.DoubleWidth ? ((x & 0x20) << 5) : 0);
					int byteAddr = (word << 1) & 0xFFFF;
					byte lo = vram[byteAddr];
					byte hi = vram[byteAddr + 1];
					int tileIndex = ((hi & 0x03) << 8) | lo;
					entries.Add((JsonNode)new JsonObject {
						["x"] = x, ["y"] = y,
						["tileIndex"] = tileIndex,
						["palette"] = (hi >> 2) & 0x07,
						["priority"] = (hi >> 5) & 0x01,
						["hFlip"] = (hi & 0x40) != 0,
						["vFlip"] = (hi & 0x80) != 0,
						["vramWord"] = (word & 0x7FFF).ToString("X4")
					});
				}
			}

			return Ok(new JsonObject {
				["layer"] = layerIdx + 1,
				["bgMode"] = bgMode,
				["bpp"] = bpp,
				["mapWidth"] = mapW,
				["mapHeight"] = mapH,
				["tileSize"] = layer.LargeTiles ? "16x16" : "8x8",
				["mapBaseAddress"] = layer.TilemapAddress.ToString("X4"),
				["charBaseAddress"] = layer.ChrAddress.ToString("X4"),
				["startX"] = startX, ["startY"] = startY,
				["width"] = width, ["height"] = height,
				["count"] = entries.Count,
				["entries"] = entries
			});
		}

		private static string CmdDecodeTiles(JsonNode root)
		{
			string source = root["source"]?.GetValue<string>()?.ToLowerInvariant() ?? "vram";
			int bpp = root["bpp"]?.GetValue<int>() ?? 4;
			int count = root["count"]?.GetValue<int>() ?? 1;
			uint addr = ParseHexOrDec(root["address"] ?? (JsonNode)0);
			int paletteOffset = root["paletteOffset"]?.GetValue<int>() ?? 0;
			string paletteSrc = root["palette"]?.GetValue<string>()?.ToLowerInvariant() ?? "grayscale";

			if(bpp != 2 && bpp != 4 && bpp != 8) return Error("bpp must be 2, 4, or 8");
			if(count < 1 || count > 4096) return Error("count must be 1..4096");

			int tileBytes = 8 * bpp; // 16, 32, 64
			int totalBytes = tileBytes * count;

			MemoryType memType;
			switch(source) {
				case "vram": memType = MemoryType.SnesVideoRam; break;
				case "rom":  memType = MemoryType.SnesPrgRom; break;
				case "wram": memType = MemoryType.SnesWorkRam; break;
				default: return Error($"Invalid source '{source}' (vram|rom|wram)");
			}

			byte[] tiles = DebugApi.GetMemoryValues(memType, addr, addr + (uint)totalBytes - 1);

			// Decode each tile into 8x8 palette-index grid.
			// SNES planar format: bitplanes are paired. 2bpp: p0/p1 interleaved by row (16B).
			// 4bpp: p0/p1 for 8 rows then p2/p3 for 8 rows (32B). 8bpp: four plane pairs (64B).
			int tilesOut = count;
			byte[,] pixels = new byte[tilesOut * 8, 8];
			for(int t = 0; t < tilesOut; t++) {
				int baseOff = t * tileBytes;
				for(int row = 0; row < 8; row++) {
					int color = 0;
					// planes 0/1
					byte p0 = tiles[baseOff + row * 2];
					byte p1 = tiles[baseOff + row * 2 + 1];
					byte p2 = 0, p3 = 0, p4 = 0, p5 = 0, p6 = 0, p7 = 0;
					if(bpp >= 4) {
						p2 = tiles[baseOff + 16 + row * 2];
						p3 = tiles[baseOff + 16 + row * 2 + 1];
					}
					if(bpp >= 8) {
						p4 = tiles[baseOff + 32 + row * 2];
						p5 = tiles[baseOff + 32 + row * 2 + 1];
						p6 = tiles[baseOff + 48 + row * 2];
						p7 = tiles[baseOff + 48 + row * 2 + 1];
					}
					for(int col = 0; col < 8; col++) {
						int bit = 7 - col;
						int idx = ((p0 >> bit) & 1)
							| (((p1 >> bit) & 1) << 1);
						if(bpp >= 4) idx |= (((p2 >> bit) & 1) << 2) | (((p3 >> bit) & 1) << 3);
						if(bpp >= 8) idx |= (((p4 >> bit) & 1) << 4) | (((p5 >> bit) & 1) << 5)
							| (((p6 >> bit) & 1) << 6) | (((p7 >> bit) & 1) << 7);
						pixels[t * 8 + row, col] = (byte)idx;
						color = idx;
					}
					_ = color;
				}
			}

			// Resolve palette colors (0xAARRGGBB, A=FF except color 0 = transparent).
			uint[] paletteRgba;
			int colorsPerTile = 1 << bpp;
			if(paletteSrc == "cgram") {
				byte[] cgram = DebugApi.GetMemoryValues(MemoryType.SnesCgRam, 0, 511);
				paletteRgba = new uint[colorsPerTile];
				for(int i = 0; i < colorsPerTile; i++) {
					int cgIdx = (paletteOffset + i) & 0xFF;
					int byteIdx = cgIdx * 2;
					ushort rgb555 = (ushort)(cgram[byteIdx] | (cgram[byteIdx + 1] << 8));
					paletteRgba[i] = Rgb555ToRgba(rgb555, i == 0);
				}
			} else {
				// grayscale — spread colorsPerTile steps across 0..255
				paletteRgba = new uint[colorsPerTile];
				for(int i = 0; i < colorsPerTile; i++) {
					byte g = (byte)(i * 255 / Math.Max(1, colorsPerTile - 1));
					paletteRgba[i] = i == 0 ? 0x00000000u : (0xFF000000u | ((uint)g << 16) | ((uint)g << 8) | g);
				}
			}

			// Build output: flat indexed pixel buffer + base64 RGBA.
			int totalPixels = tilesOut * 8 * 8;
			byte[] indexed = new byte[totalPixels];
			byte[] rgba = new byte[totalPixels * 4];
			for(int t = 0; t < tilesOut; t++) {
				for(int row = 0; row < 8; row++) {
					for(int col = 0; col < 8; col++) {
						byte idx = pixels[t * 8 + row, col];
						int pixel = (t * 64) + row * 8 + col;
						indexed[pixel] = idx;
						uint argb = idx < paletteRgba.Length ? paletteRgba[idx] : 0;
						rgba[pixel * 4 + 0] = (byte)((argb >> 16) & 0xFF); // R
						rgba[pixel * 4 + 1] = (byte)((argb >> 8) & 0xFF);  // G
						rgba[pixel * 4 + 2] = (byte)(argb & 0xFF);          // B
						rgba[pixel * 4 + 3] = (byte)((argb >> 24) & 0xFF); // A
					}
				}
			}

			return Ok(new JsonObject {
				["source"] = source,
				["address"] = addr.ToString("X6"),
				["bpp"] = bpp,
				["tileCount"] = tilesOut,
				["tileWidth"] = 8,
				["tileHeight"] = 8,
				["paletteSource"] = paletteSrc,
				["paletteOffset"] = paletteOffset,
				["indexed"] = BytesToJsonArray(indexed),
				["rgbaBase64"] = Convert.ToBase64String(rgba)
			});
		}

		private static uint Rgb555ToRgba(ushort rgb555, bool transparent)
		{
			int r = ((rgb555 & 0x1F) << 3) | ((rgb555 & 0x1F) >> 2);
			int g = (((rgb555 >> 5) & 0x1F) << 3) | (((rgb555 >> 5) & 0x1F) >> 2);
			int b = (((rgb555 >> 10) & 0x1F) << 3) | (((rgb555 >> 10) & 0x1F) >> 2);
			uint a = transparent ? 0u : 0xFFu;
			return (a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
		}

		private static string CmdRenderBgLayer(JsonNode root)
		{
			int? layerParam = root["layer"]?.GetValue<int>();
			if(layerParam == null || layerParam < 1 || layerParam > 4) return Error("Missing/invalid 'layer' (1-4)");
			int layerIdx = layerParam.Value - 1;

			var ppu = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);
			var layer = ppu.Layers[layerIdx];
			int bgMode = ppu.BgMode & 0x07;
			int bpp = (bgMode < _modeBpp.Length) ? _modeBpp[bgMode][layerIdx] : 0;
			if(bpp == 0) return Error($"Layer {layerParam} disabled in BG mode {bgMode}");

			int mapW = layer.DoubleWidth ? 64 : 32;
			int mapH = layer.DoubleHeight ? 64 : 32;
			int tilePx = layer.LargeTiles ? 16 : 8;
			int outW = mapW * tilePx;
			int outH = mapH * tilePx;

			if(outW * outH > 1024 * 1024) return Error("Output exceeds 1024x1024 pixel cap");

			byte[] vram = DebugApi.GetMemoryValues(MemoryType.SnesVideoRam, 0, 0xFFFF);
			byte[] cgram = DebugApi.GetMemoryValues(MemoryType.SnesCgRam, 0, 511);

			int tileBytes = 8 * bpp;
			int chrByteBase = (layer.ChrAddress << 1) & 0xFFFF;
			int colorsPerPalette = 1 << bpp;

			byte[] rgba = new byte[outW * outH * 4];

			// Background color from CGRAM[0] (transparent pixels still show this for the bottom layer).
			uint bgColor = Rgb555ToRgba((ushort)(cgram[0] | (cgram[1] << 8)), transparent: false);

			for(int my = 0; my < mapH; my++) {
				int vOff = layer.DoubleHeight ? ((my & 0x20) << (layer.DoubleWidth ? 6 : 5)) : 0;
				int rowBaseWord = layer.TilemapAddress + vOff + ((my & 0x1F) << 5);

				for(int mx = 0; mx < mapW; mx++) {
					int word = rowBaseWord + (mx & 0x1F) + (layer.DoubleWidth ? ((mx & 0x20) << 5) : 0);
					int byteAddr = (word << 1) & 0xFFFF;
					byte lo = vram[byteAddr];
					byte hi = vram[byteAddr + 1];
					int tileIndex = ((hi & 0x03) << 8) | lo;
					int palette = (hi >> 2) & 0x07;
					bool hFlip = (hi & 0x40) != 0;
					bool vFlip = (hi & 0x80) != 0;

					RenderBgTileBlock(vram, cgram, rgba, outW, outH,
						mx * tilePx, my * tilePx, tilePx,
						chrByteBase, tileIndex, bpp, palette,
						hFlip, vFlip, colorsPerPalette, bgColor);
				}
			}

			return Ok(new JsonObject {
				["layer"] = layerIdx + 1,
				["bgMode"] = bgMode,
				["bpp"] = bpp,
				["width"] = outW,
				["height"] = outH,
				["tileSize"] = layer.LargeTiles ? "16x16" : "8x8",
				["mapWidth"] = mapW,
				["mapHeight"] = mapH,
				["mapBaseAddress"] = layer.TilemapAddress.ToString("X4"),
				["charBaseAddress"] = layer.ChrAddress.ToString("X4"),
				["rgbaBase64"] = Convert.ToBase64String(rgba)
			});
		}

		private static void RenderBgTileBlock(byte[] vram, byte[] cgram, byte[] rgba,
			int outW, int outH, int dstX, int dstY, int tilePx,
			int chrByteBase, int tileIndex, int bpp, int palette,
			bool hFlip, bool vFlip, int colorsPerPalette, uint bgColor)
		{
			// Large tile: 2x2 sub-tiles arranged [t, t+1; t+16, t+17] with flips reordering.
			int subTiles = tilePx == 16 ? 2 : 1;
			int paletteByteBase = bpp == 8 ? 0 : palette * colorsPerPalette * 2;

			for(int sy = 0; sy < subTiles; sy++) {
				for(int sx = 0; sx < subTiles; sx++) {
					int subYOff = (subTiles == 2) ? ((sy ^ (vFlip ? 1 : 0)) * 16) : 0;
					int subXOff = (subTiles == 2) ? ((sx ^ (hFlip ? 1 : 0)) * 1) : 0;
					int subTileIndex = (tileIndex + subYOff + subXOff) & 0x3FF;
					int tileByteStart = (chrByteBase + subTileIndex * (8 * bpp)) & 0xFFFF;

					for(int row = 0; row < 8; row++) {
						int srcRow = vFlip ? (7 - row) : row;
						int rowOff = (tileByteStart + srcRow * 2) & 0xFFFF;
						byte p0 = vram[rowOff], p1 = vram[(rowOff + 1) & 0xFFFF];
						byte p2 = 0, p3 = 0, p4 = 0, p5 = 0, p6 = 0, p7 = 0;
						if(bpp >= 4) {
							int o2 = (tileByteStart + 16 + srcRow * 2) & 0xFFFF;
							p2 = vram[o2]; p3 = vram[(o2 + 1) & 0xFFFF];
						}
						if(bpp >= 8) {
							int o4 = (tileByteStart + 32 + srcRow * 2) & 0xFFFF;
							int o6 = (tileByteStart + 48 + srcRow * 2) & 0xFFFF;
							p4 = vram[o4]; p5 = vram[(o4 + 1) & 0xFFFF];
							p6 = vram[o6]; p7 = vram[(o6 + 1) & 0xFFFF];
						}

						for(int col = 0; col < 8; col++) {
							int srcCol = hFlip ? (7 - col) : col;
							int bit = 7 - srcCol;
							int idx = ((p0 >> bit) & 1) | (((p1 >> bit) & 1) << 1);
							if(bpp >= 4) idx |= (((p2 >> bit) & 1) << 2) | (((p3 >> bit) & 1) << 3);
							if(bpp >= 8) idx |= (((p4 >> bit) & 1) << 4) | (((p5 >> bit) & 1) << 5)
								| (((p6 >> bit) & 1) << 6) | (((p7 >> bit) & 1) << 7);

							int px = dstX + sx * 8 + col;
							int py = dstY + sy * 8 + row;
							if((uint)px >= (uint)outW || (uint)py >= (uint)outH) continue;
							int dstOff = (py * outW + px) * 4;

							uint argb;
							if(idx == 0) {
								// Transparent pixel: fall back to BG color (CGRAM[0]) with alpha 0
								// so callers can composite or detect.
								argb = bgColor & 0x00FFFFFFu;
							} else {
								int cgIdx = (paletteByteBase + idx * 2) & 0x1FF;
								ushort rgb555 = (ushort)(cgram[cgIdx] | (cgram[cgIdx + 1] << 8));
								argb = Rgb555ToRgba(rgb555, transparent: false);
							}
							rgba[dstOff + 0] = (byte)((argb >> 16) & 0xFF);
							rgba[dstOff + 1] = (byte)((argb >> 8) & 0xFF);
							rgba[dstOff + 2] = (byte)(argb & 0xFF);
							rgba[dstOff + 3] = (byte)((argb >> 24) & 0xFF);
						}
					}
				}
			}
		}

		private static string CmdRunUntilVramWrite(JsonNode root)
		{
			uint vramStart = ParseHexOrDec(root["vramAddress"] ?? (JsonNode)0);
			uint vramEnd = root["vramEndAddress"] != null ? ParseHexOrDec(root["vramEndAddress"]) : vramStart;
			int timeoutMs = root["timeout"]?.GetValue<int>() ?? 10000;
			if(timeoutMs < 100) timeoutMs = 100;
			if(timeoutMs > 60000) timeoutMs = 60000;
			if(vramEnd < vramStart) return Error("vramEndAddress < vramAddress");
			if(vramEnd > 0xFFFF) return Error("VRAM address out of range (0..0xFFFF)");

			EnsureEventListener();

			// Temp breakpoint on writes to $2118/$2119 (VRAM data low/high).
			Breakpoint? bp = null;
			RunOnUiThread(() => {
				bp = new Breakpoint {
					StartAddress = 0x2118,
					EndAddress = 0x2119,
					MemoryType = MemoryType.SnesMemory,
					CpuType = CpuType.Snes,
					BreakOnRead = false,
					BreakOnWrite = true,
					BreakOnExec = false,
					Enabled = true
				};
				BreakpointManager.AddBreakpoint(bp);
				BreakpointManager.MarkAsIpcSet(bp);
			});

			using var signal = new ManualResetEventSlim(false);
			ConsoleNotificationType? firedType = null;
			void handler(NotificationEventArgs e)
			{
				if(e.NotificationType == ConsoleNotificationType.CodeBreak) {
					firedType = e.NotificationType;
					signal.Set();
				}
			}
			_eventListener!.OnNotification += handler;

			DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			try {
				EmuApi.Resume();
				while(true) {
					int remain = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
					if(remain == 0) {
						return Ok(new JsonObject {
							["triggered"] = false, ["timedOut"] = true,
							["vramAddress"] = null, ["matchedRange"] = false
						});
					}
					signal.Reset();
					if(!signal.Wait(remain)) {
						return Ok(new JsonObject {
							["triggered"] = false, ["timedOut"] = true,
							["vramAddress"] = null, ["matchedRange"] = false
						});
					}

					var ppu = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);
					ushort vramAddr = ppu.VramAddress;
					var cpu = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
					uint pc = (uint)((cpu.K << 16) | cpu.PC);

					if(vramAddr >= vramStart && vramAddr <= vramEnd) {
						return Ok(new JsonObject {
							["triggered"] = true, ["timedOut"] = false,
							["matchedRange"] = true,
							["vramAddress"] = vramAddr.ToString("X4"),
							["pc"] = pc.ToString("X6"),
							["cpuState"] = new JsonObject {
								["a"] = cpu.A.ToString("X4"),
								["x"] = cpu.X.ToString("X4"),
								["y"] = cpu.Y.ToString("X4"),
								["sp"] = cpu.SP.ToString("X4"),
								["pc"] = pc.ToString("X6"),
								["d"] = cpu.D.ToString("X4"),
								["dbr"] = cpu.DBR.ToString("X2"),
								["k"] = cpu.K.ToString("X2"),
								["flags"] = cpu.PS.ToString("X2")
							}
						});
					}

					// Not in target VRAM range — resume and keep waiting.
					EmuApi.Resume();
				}
			} finally {
				_eventListener!.OnNotification -= handler;
				if(bp != null) {
					RunOnUiThread(() => BreakpointManager.RemoveBreakpoint(bp));
				}
			}
		}

		// ── Memory Search & Diff ─────────────────────────────────────────────

		private static string CmdSearchMemory(JsonNode root)
		{
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			string? pattern = root["pattern"]?.GetValue<string>();
			if(string.IsNullOrWhiteSpace(pattern)) return Error("Missing 'pattern' (hex bytes, ?? for wildcard)");

			uint startAddr = ParseHexOrDec(root["startAddress"] ?? (JsonNode)0);
			int maxResults = root["maxResults"]?.GetValue<int>() ?? 20;
			if(maxResults < 1) maxResults = 1;
			if(maxResults > 1000) maxResults = 1000;

			// Parse pattern: "B4 ?? 4A D6" → bytes + mask
			string[] tokens = pattern.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
			if(tokens.Length == 0) return Error("Empty pattern");
			if(tokens.Length > 256) return Error("Pattern too long (max 256 bytes)");

			byte[] patBytes = new byte[tokens.Length];
			bool[] mask = new bool[tokens.Length]; // true = must match
			for(int i = 0; i < tokens.Length; i++) {
				if(tokens[i] == "??" || tokens[i] == "**") {
					mask[i] = false;
				} else {
					if(!byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out patBytes[i]))
						return Error($"Invalid hex byte in pattern: '{tokens[i]}'");
					mask[i] = true;
				}
			}

			int memSize = DebugApi.GetMemorySize(memType);
			if(memSize == 0) return Error($"Memory type '{memType}' not available or size is 0");
			if(startAddr >= (uint)memSize) return Error("startAddress beyond memory size");

			// Read entire memory region (capped at memSize)
			uint readLen = (uint)memSize - startAddr;
			if(readLen > 0x200000) readLen = 0x200000; // Cap at 2MB per search
			byte[] mem = DebugApi.GetMemoryValues(memType, startAddr, startAddr + readLen - 1);

			var results = new JsonArray();
			int patLen = patBytes.Length;
			int searchEnd = mem.Length - patLen;

			for(int pos = 0; pos <= searchEnd && results.Count < maxResults; pos++) {
				bool match = true;
				for(int j = 0; j < patLen; j++) {
					if(mask[j] && mem[pos + j] != patBytes[j]) {
						match = false;
						break;
					}
				}
				if(match) {
					byte[] found = new byte[patLen];
					Array.Copy(mem, pos, found, 0, patLen);
					results.Add((JsonNode)new JsonObject {
						["address"] = (startAddr + (uint)pos).ToString("X6"),
						["hex"] = BitConverter.ToString(found).Replace("-", " ")
					});
				}
			}

			return Ok(new JsonObject {
				["memoryType"] = memType.ToString(),
				["pattern"] = pattern,
				["count"] = results.Count,
				["results"] = results
			});
		}

		// Snapshot storage: id → (memType, address, data, timestamp)
		private static readonly Dictionary<string, (MemoryType memType, uint address, byte[] data, DateTime created)> _snapshots = new();
		private static readonly object _snapshotLock = new();
		private const int MaxSnapshots = 16;
		private const int MaxSnapshotSize = 0x10000; // 64KB
		private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(5);

		private static void PurgeExpiredSnapshots()
		{
			var now = DateTime.UtcNow;
			var expired = new List<string>();
			foreach(var kv in _snapshots) {
				if(now - kv.Value.created > SnapshotTtl) expired.Add(kv.Key);
			}
			foreach(var k in expired) _snapshots.Remove(k);
		}

		private static string CmdSnapshotMemory(JsonNode root)
		{
			string? id = root["id"]?.GetValue<string>();
			if(string.IsNullOrWhiteSpace(id)) return Error("Missing 'id'");

			MemoryType memType = ParseMemoryType(root["memoryType"]);
			uint address = ParseHexOrDec(root["address"] ?? (JsonNode)0);
			uint length = (uint)(root["length"]?.GetValue<int>() ?? 256);

			if(length > MaxSnapshotSize) return Error($"Length exceeds maximum ({MaxSnapshotSize} bytes)");

			byte[] data = DebugApi.GetMemoryValues(memType, address, address + length - 1);

			lock(_snapshotLock) {
				PurgeExpiredSnapshots();
				if(!_snapshots.ContainsKey(id) && _snapshots.Count >= MaxSnapshots)
					return Error($"Too many snapshots (max {MaxSnapshots}). Use clearSnapshots or wait for expiry.");
				_snapshots[id] = (memType, address, data, DateTime.UtcNow);
			}

			return Ok(new JsonObject {
				["id"] = id,
				["memoryType"] = memType.ToString(),
				["address"] = address.ToString("X6"),
				["length"] = data.Length
			});
		}

		private static string CmdDiffMemory(JsonNode root)
		{
			string? id = root["snapshotId"]?.GetValue<string>();
			if(string.IsNullOrWhiteSpace(id)) return Error("Missing 'snapshotId'");

			(MemoryType memType, uint address, byte[] oldData, DateTime created) snapshot;
			lock(_snapshotLock) {
				PurgeExpiredSnapshots();
				if(!_snapshots.TryGetValue(id, out snapshot))
					return Error($"Snapshot '{id}' not found or expired");
			}

			// Allow override of memoryType/address, default to snapshot's values
			MemoryType memType = root["memoryType"] != null ? ParseMemoryType(root["memoryType"]) : snapshot.memType;
			uint address = root["address"] != null ? ParseHexOrDec(root["address"]) : snapshot.address;
			uint length = (uint)(root["length"]?.GetValue<int>() ?? snapshot.oldData.Length);

			if(length > MaxSnapshotSize) return Error($"Length exceeds maximum ({MaxSnapshotSize} bytes)");

			byte[] newData = DebugApi.GetMemoryValues(memType, address, address + length - 1);
			byte[] oldData = snapshot.oldData;

			// Compare — build change runs
			int compareLen = Math.Min(oldData.Length, newData.Length);
			var changes = new JsonArray();
			int totalChanged = 0;
			int i = 0;

			while(i < compareLen) {
				if(oldData[i] != newData[i]) {
					int runStart = i;
					while(i < compareLen && oldData[i] != newData[i]) i++;
					int runLen = i - runStart;
					totalChanged += runLen;

					byte[] oldRun = new byte[runLen];
					byte[] newRun = new byte[runLen];
					Array.Copy(oldData, runStart, oldRun, 0, runLen);
					Array.Copy(newData, runStart, newRun, 0, runLen);

					changes.Add((JsonNode)new JsonObject {
						["offset"] = runStart,
						["address"] = (address + (uint)runStart).ToString("X6"),
						["length"] = runLen,
						["oldHex"] = BitConverter.ToString(oldRun).Replace("-", " "),
						["newHex"] = BitConverter.ToString(newRun).Replace("-", " ")
					});
				} else {
					i++;
				}
			}

			return Ok(new JsonObject {
				["snapshotId"] = id,
				["memoryType"] = memType.ToString(),
				["address"] = address.ToString("X6"),
				["totalBytes"] = compareLen,
				["totalChanged"] = totalChanged,
				["totalUnchanged"] = compareLen - totalChanged,
				["changeCount"] = changes.Count,
				["changes"] = changes
			});
		}

		private static string CmdClearSnapshots()
		{
			lock(_snapshotLock) {
				int count = _snapshots.Count;
				_snapshots.Clear();
				return Ok(new JsonObject { ["cleared"] = count });
			}
		}

		// ── Trace Log & Event Wait ───────────────────────────────────────────

		private static bool _traceLogEnabledByIpc = false;

		private static string CmdGetTraceLog(JsonNode root)
		{
			int count = root["count"]?.GetValue<int>() ?? 100;
			if(count < 1) count = 1;
			if(count > 30000) count = 30000;

			CpuType? filterCpu = null;
			if(root["cpuType"] != null) {
				filterCpu = ParseCpuType(root["cpuType"]);
			}

			uint traceSize = DebugApi.GetExecutionTraceSize();
			if(traceSize == 0) {
				return Ok(new JsonObject {
					["count"] = 0,
					["entries"] = new JsonArray(),
					["hint"] = "Trace log empty. Ensure trace logging is enabled via setTraceLogEnabled or the Trace Logger UI window."
				});
			}

			TraceRow[] rows = DebugApi.GetExecutionTrace(0, (uint)Math.Min(count, (int)traceSize));

			var entries = new JsonArray();
			foreach(var row in rows) {
				if(filterCpu != null && row.Type != filterCpu.Value) continue;

				byte[] byteCode = row.GetByteCode();
				entries.Add((JsonNode)new JsonObject {
					["pc"] = row.ProgramCounter.ToString("X6"),
					["cpuType"] = row.Type.ToString(),
					["byteCode"] = BitConverter.ToString(byteCode).Replace("-", " "),
					["log"] = row.GetOutput().TrimEnd()
				});
			}

			return Ok(new JsonObject {
				["count"] = entries.Count,
				["totalAvailable"] = (int)traceSize,
				["entries"] = entries
			});
		}

		private static string CmdSetTraceLogEnabled(JsonNode root)
		{
			bool enabled = root["enabled"]?.GetValue<bool>() ?? true;
			CpuType cpuType = ParseCpuType(root["cpuType"]);

			string format = "[Disassembly][Align,24] A:[A,4h] X:[X,4h] Y:[Y,4h] S:[SP,4h] D:[D,4h] DB:[DB,2h] P:[P,h]";
			if(root["format"] != null) {
				format = root["format"]!.GetValue<string>();
			}

			var options = new InteropTraceLoggerOptions {
				Enabled = enabled,
				UseLabels = root["useLabels"]?.GetValue<bool>() ?? true,
				IndentCode = root["indentCode"]?.GetValue<bool>() ?? false,
				Format = Encoding.UTF8.GetBytes(format),
				Condition = Encoding.UTF8.GetBytes(root["condition"]?.GetValue<string>() ?? "")
			};

			Array.Resize(ref options.Format, 1000);
			Array.Resize(ref options.Condition, 1000);

			DebugApi.SetTraceOptions(cpuType, options);
			_traceLogEnabledByIpc = enabled;

			return Ok(new JsonObject {
				["enabled"] = enabled,
				["cpuType"] = cpuType.ToString()
			});
		}

		// waitForEvent: blocks until a debugger notification fires or timeout
		private static NotificationListener? _eventListener;
		private static readonly object _eventListenerLock = new();

		private static void EnsureEventListener()
		{
			if(_eventListener != null) return;
			lock(_eventListenerLock) {
				if(_eventListener != null) return;
				_eventListener = new NotificationListener();
			}
		}

		private static string CmdWaitForEvent(JsonNode root)
		{
			string? eventName = root["event"]?.GetValue<string>();
			if(string.IsNullOrWhiteSpace(eventName))
				return Error("Missing 'event' (breakpoint, paused, resumed, frameComplete)");

			int timeout = root["timeout"]?.GetValue<int>() ?? 5000;
			if(timeout < 100) timeout = 100;
			if(timeout > 60000) timeout = 60000;

			ConsoleNotificationType? targetType = eventName.ToLowerInvariant() switch {
				"breakpoint" or "codebreak" => ConsoleNotificationType.CodeBreak,
				"paused" or "gamepaused" => ConsoleNotificationType.GamePaused,
				"resumed" or "gameresumed" => ConsoleNotificationType.GameResumed,
				"debuggerresumed" => ConsoleNotificationType.DebuggerResumed,
				"framecomplete" or "ppuframedone" => ConsoleNotificationType.PpuFrameDone,
				"romloaded" or "gameloaded" => ConsoleNotificationType.GameLoaded,
				"stateloaded" => ConsoleNotificationType.StateLoaded,
				"reset" or "gamereset" => ConsoleNotificationType.GameReset,
				_ => null
			};

			if(targetType == null)
				return Error($"Unknown event type: '{eventName}'. Valid: breakpoint, paused, resumed, debuggerResumed, frameComplete, romLoaded, stateLoaded, reset");

			EnsureEventListener();

			using var signal = new ManualResetEventSlim(false);
			ConsoleNotificationType? firedType = null;

			void handler(NotificationEventArgs e)
			{
				if(e.NotificationType == targetType.Value) {
					firedType = e.NotificationType;
					signal.Set();
				}
			}

			_eventListener!.OnNotification += handler;
			try {
				bool triggered = signal.Wait(timeout);

				var result = new JsonObject {
					["triggered"] = triggered,
					["event"] = triggered ? firedType.ToString() : null,
					["timedOut"] = !triggered
				};

				// If breakpoint, include CPU state
				if(triggered && targetType == ConsoleNotificationType.CodeBreak) {
					try {
						var cpu = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
						result["cpuState"] = new JsonObject {
							["a"] = cpu.A.ToString("X4"),
							["x"] = cpu.X.ToString("X4"),
							["y"] = cpu.Y.ToString("X4"),
							["sp"] = cpu.SP.ToString("X4"),
							["pc"] = ((cpu.K << 16) | cpu.PC).ToString("X6"),
							["d"] = cpu.D.ToString("X4"),
							["dbr"] = cpu.DBR.ToString("X2"),
							["k"] = cpu.K.ToString("X2"),
							["flags"] = cpu.PS.ToString("X2")
						};
					} catch { }
				}

				return Ok(result);
			} finally {
				_eventListener!.OnNotification -= handler;
			}
		}

		// ── IPC Memory Watch Hook ────────────────────────────────────────────

		// Tracks current range list per CpuType so "add" commands can append.
		private static readonly Dictionary<CpuType, List<IpcWatchRange>> _memoryWatches = new();
		private static readonly object _memoryWatchLock = new();
		private static bool _ringSizeApplied = false;

		private static void EnsureRingSizeApplied()
		{
			if(_ringSizeApplied) return;
			_ringSizeApplied = true;
			uint size = ConfigManager.Config.Debug.Ipc.MemoryWatchRingSize;
			if(size > 0) {
				DebugApi.SetIpcMemoryRingSize(size);
			}
		}

		private static UInt32 ParseOpMask(JsonNode? opsNode)
		{
			if(opsNode == null) return IpcWatchOpMaskBits.AllAccess;
			var arr = opsNode.AsArray();
			UInt32 mask = 0;
			foreach(var op in arr) {
				if(op == null) continue;
				string s = op.GetValue<string>().ToLowerInvariant();
				mask |= s switch {
					"read" => IpcWatchOpMaskBits.Read,
					"write" => IpcWatchOpMaskBits.Write,
					"exec" or "execopcode" => IpcWatchOpMaskBits.ExecOpCode,
					"execoperand" => IpcWatchOpMaskBits.ExecOperand,
					"dmaread" => IpcWatchOpMaskBits.DmaRead,
					"dmawrite" => IpcWatchOpMaskBits.DmaWrite,
					"all" => IpcWatchOpMaskBits.AllWithExec,
					"allaccess" => IpcWatchOpMaskBits.AllAccess,
					_ => 0u
				};
			}
			return mask == 0 ? IpcWatchOpMaskBits.AllAccess : mask;
		}

		private static List<IpcWatchRange> ParseRanges(JsonNode? rangesNode)
		{
			var list = new List<IpcWatchRange>();
			if(rangesNode == null) return list;
			foreach(var r in rangesNode.AsArray()) {
				if(r == null) continue;
				uint start = ParseHexOrDec(r["start"]);
				uint end = r["end"] != null ? ParseHexOrDec(r["end"]) : start;
				UInt32 mask = ParseOpMask(r["ops"]);
				ushort valMin = r["valueMin"] != null ? (ushort)(ParseHexOrDec(r["valueMin"]) & 0xFFFF) : (ushort)0;
				ushort valMax = r["valueMax"] != null ? (ushort)(ParseHexOrDec(r["valueMax"]) & 0xFFFF) : (ushort)0xFFFF;
				uint sampleRate = r["sampleRate"] != null ? ParseHexOrDec(r["sampleRate"]) : 0u;
				list.Add(new IpcWatchRange {
					Start = start, End = end, OpMask = mask,
					ValueMin = valMin, ValueMax = valMax,
					SampleRate = sampleRate, SampleCounter = 0
				});
			}
			return list;
		}

		private static void ApplyWatches(CpuType cpu)
		{
			var list = _memoryWatches.TryGetValue(cpu, out var l) ? l : new List<IpcWatchRange>();
			DebugApi.SetIpcMemoryWatches(cpu, list.ToArray(), (uint)list.Count);
		}

		private static string CmdWatchCpuMemory(JsonNode root)
		{
			CpuType cpu = ParseCpuType(root["cpuType"]);
			List<IpcWatchRange> ranges = ParseRanges(root["ranges"]);
			UInt32 defaultMask = ParseOpMask(root["ops"]);
			for(int i = 0; i < ranges.Count; i++) {
				if(ranges[i].OpMask == IpcWatchOpMaskBits.AllAccess && root["ops"] != null) {
					var r = ranges[i]; r.OpMask = defaultMask; ranges[i] = r;
				}
			}

			lock(_memoryWatchLock) {
				EnsureRingSizeApplied();
				_memoryWatches[cpu] = ranges;
				ApplyWatches(cpu);
			}
			DebugApi.SetIpcMemoryWatchEnabled(true);
			return Ok(new JsonObject { ["cpuType"] = cpu.ToString(), ["count"] = ranges.Count });
		}

		private static string CmdAddCpuMemoryWatch(JsonNode root)
		{
			CpuType cpu = ParseCpuType(root["cpuType"]);
			uint start = ParseHexOrDec(root["start"]);
			uint end = root["end"] != null ? ParseHexOrDec(root["end"]) : start;
			UInt32 mask = ParseOpMask(root["ops"]);
			ushort valMin = root["valueMin"] != null ? (ushort)(ParseHexOrDec(root["valueMin"]) & 0xFFFF) : (ushort)0;
			ushort valMax = root["valueMax"] != null ? (ushort)(ParseHexOrDec(root["valueMax"]) & 0xFFFF) : (ushort)0xFFFF;
			uint sampleRate = root["sampleRate"] != null ? ParseHexOrDec(root["sampleRate"]) : 0u;

			lock(_memoryWatchLock) {
				EnsureRingSizeApplied();
				if(!_memoryWatches.TryGetValue(cpu, out var list)) {
					list = new List<IpcWatchRange>();
					_memoryWatches[cpu] = list;
				}
				list.Add(new IpcWatchRange {
					Start = start, End = end, OpMask = mask,
					ValueMin = valMin, ValueMax = valMax,
					SampleRate = sampleRate, SampleCounter = 0
				});
				ApplyWatches(cpu);
			}
			DebugApi.SetIpcMemoryWatchEnabled(true);
			return Ok(new JsonObject { ["cpuType"] = cpu.ToString(), ["count"] = _memoryWatches[cpu].Count });
		}

		private static string CmdClearCpuMemoryWatches()
		{
			lock(_memoryWatchLock) {
				_memoryWatches.Clear();
			}
			DebugApi.ClearIpcMemoryWatches();
			return Ok();
		}

		private static readonly IpcMemEvent[] _pollBuffer = new IpcMemEvent[4096];

		private static string CmdPollMemoryEvents(JsonNode root)
		{
			int maxEvents = root["maxEvents"]?.GetValue<int>() ?? _pollBuffer.Length;
			if(maxEvents < 1) maxEvents = 1;
			if(maxEvents > _pollBuffer.Length) maxEvents = _pollBuffer.Length;

			uint count = DebugApi.PollIpcMemoryEvents(_pollBuffer, (uint)maxEvents, out UInt64 dropped, out UInt64 highWater);

			var events = new JsonArray();
			for(uint i = 0; i < count; i++) {
				var e = _pollBuffer[i];
				events.Add((JsonNode)new JsonObject {
					["masterClock"] = e.MasterClock,
					["cpuType"] = ((CpuType)e.CpuType).ToString(),
					["address"] = e.Address,
					["absAddress"] = e.AbsAddress == UInt32.MaxValue ? null : (JsonNode)e.AbsAddress,
					["value"] = e.Value,
					["opType"] = ((MemoryOperationType)e.OpType).ToString(),
					["accessWidth"] = e.AccessWidth
				});
			}

			return Ok(new JsonObject {
				["count"] = (int)count,
				["dropped"] = dropped,
				["highWater"] = highWater,
				["events"] = events
			});
		}

		private static string CmdSetMemoryWatchEnabled(JsonNode root)
		{
			bool enabled = root["enabled"]?.GetValue<bool>() ?? false;
			if(enabled) EnsureRingSizeApplied();
			DebugApi.SetIpcMemoryWatchEnabled(enabled);
			return Ok(new JsonObject { ["enabled"] = enabled });
		}

		private static string CmdSetMemoryWatchRingSize(JsonNode root)
		{
			uint size = (uint?)root["size"]?.GetValue<long>() ?? 0u;
			if(size == 0) return Error("Missing 'size' (events; power of 2, 1024..4194304)");
			DebugApi.SetIpcMemoryRingSize(size);
			ConfigManager.Config.Debug.Ipc.MemoryWatchRingSize = size;
			_ringSizeApplied = true;
			return Ok(new JsonObject { ["size"] = size });
		}
	}
}
