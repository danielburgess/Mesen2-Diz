using Avalonia.Threading;
using Mesen.Config;
using Mesen.Debugger;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

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
					"getProgramCounter" => CmdGetProgramCounter(root),
					"setProgramCounter" => CmdSetProgramCounter(root),

					// Execution Control
					"pause" => CmdPause(),
					"resume" => CmdResume(),
					"isPaused" => CmdIsPaused(),
					"step" => CmdStep(root),

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

		private static string CmdSetLabel(JsonNode root)
		{
			uint address = ParseHexOrDec(root["address"]);
			MemoryType memType = ParseMemoryType(root["memoryType"]);
			string label = root["label"]?.GetValue<string>() ?? "";
			string comment = root["comment"]?.GetValue<string>() ?? "";
			uint length = (uint)(root["length"]?.GetValue<int>() ?? 1);
			string? categoryStr = root["category"]?.GetValue<string>();

			FunctionCategory category = FunctionCategory.None;
			if(!string.IsNullOrEmpty(categoryStr)) {
				Enum.TryParse(categoryStr, true, out category);
			}

			RunOnUiThread(() => {
				var codeLabel = new CodeLabel {
					Address = address,
					MemoryType = memType,
					Label = label,
					Comment = comment,
					Length = length,
					Category = category
				};
				LabelManager.SetLabel(codeLabel, true);
			});

			return Ok(new JsonObject {
				["address"] = address.ToString("X6"),
				["label"] = label,
				["comment"] = comment
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
				return Ok(new JsonObject {
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
				});
			}

			uint pc = DebugApi.GetProgramCounter(cpuType, true);
			return Ok(new JsonObject {
				["cpuType"] = cpuType.ToString(),
				["pc"] = pc.ToString("X6")
			});
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
			string? typeStr = root["stepType"]?.GetValue<string>();

			StepType stepType = StepType.Step;
			if(!string.IsNullOrEmpty(typeStr)) {
				Enum.TryParse(typeStr, true, out stepType);
			}

			DebugApi.Step(cpuType, count, stepType);
			return Ok(new JsonObject {
				["cpuType"] = cpuType.ToString(),
				["count"] = count,
				["stepType"] = stepType.ToString()
			});
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
	}
}
