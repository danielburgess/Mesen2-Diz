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
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mesen.Utilities
{
	public static class IpcCommandHandler
	{
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

					// IPC info
					"getIpcInfo" => CmdGetIpcInfo(),

					// Cheats
					"setCheats" => CmdSetCheats(root),
					"clearCheats" => CmdClearCheats(),

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
			EmuApi.Resume();
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
	}
}
