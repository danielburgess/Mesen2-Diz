using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Mesen.Config;
using Mesen.Debugger.AI;
using Mesen.Interop;
using Mesen.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Mesen.Debugger.ViewModels
{
	public class ChatEntry : ReactiveObject
	{
		public enum EntryKind { User, Assistant, ToolStatus, System, Error }

		[Reactive] public EntryKind Kind { get; set; }
		[Reactive] public string Text { get; set; } = "";
		[Reactive] public bool IsStreaming { get; set; }
		[Reactive] public string StreamingIndicator { get; private set; } = ".";
		public DateTime Timestamp { get; init; } = DateTime.Now;

		public bool IsUser => Kind == EntryKind.User;
		public bool IsAssistant => Kind == EntryKind.Assistant;
		public bool IsToolStatus => Kind == EntryKind.ToolStatus;
		public bool IsSystem => Kind == EntryKind.System;
		public bool IsError => Kind == EntryKind.Error;

		public string TimestampDisplay => Timestamp.ToString("HH:mm:ss");

		public void AppendText(string delta) => Text += delta;

		private static readonly string[] _frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
		private Avalonia.Threading.DispatcherTimer? _streamTimer;
		private int _frameIndex;

		public ChatEntry()
		{
			this.WhenAnyValue(x => x.IsStreaming).Subscribe(streaming => {
				// Always manipulate the timer on the UI thread — ChatEntry may be constructed
				// on a background thread and IsStreaming set via object initializer before
				// it's added to the UI collection.
				Avalonia.Threading.Dispatcher.UIThread.Post(() => {
					if(streaming) {
						_frameIndex = 0;
						StreamingIndicator = _frames[0];
						_streamTimer = new Avalonia.Threading.DispatcherTimer {
							Interval = TimeSpan.FromMilliseconds(80)
						};
						_streamTimer.Tick += (_, _) => {
							_frameIndex = (_frameIndex + 1) % _frames.Length;
							StreamingIndicator = _frames[_frameIndex];
						};
						_streamTimer.Start();
					} else {
						_streamTimer?.Stop();
						_streamTimer = null;
					}
				});
			});
		}
	}

	public class ChatHistoryEntry
	{
		public DateTime Timestamp { get; init; }
		public string FilePath { get; init; } = "";
		public string DisplayText => Timestamp.ToString("yyyy-MM-dd  HH:mm:ss");
	}

	public class AiCompanionViewModel : DisposableViewModel
	{
		// ── Observables ───────────────────────────────────────────────────────

		[Reactive] public bool IsBusy { get; private set; }
		[Reactive] public string InputText { get; set; } = "";
		[Reactive] public string StatusText { get; private set; } = "Ready";
		[Reactive] public string ContextStatusText { get; private set; } = "no context";
		[Reactive] public bool ChatScrollLocked { get; set; } = true;

		// History viewer state
		[Reactive] public bool IsViewingHistory { get; private set; }
		[Reactive] public string HistorySessionLabel { get; private set; } = "";
		[Reactive] public bool HasArchivedSessions { get; private set; }
		[Reactive] public ObservableCollection<ChatEntry> ActiveMessages { get; private set; } = null!;

		public ObservableCollection<ChatEntry> Messages { get; } = new();
		public ObservableCollection<ChatEntry> ToolCallLog { get; } = new();
		public ObservableCollection<ChatHistoryEntry> ArchivedSessions { get; } = new();

		// ── Commands ──────────────────────────────────────────────────────────

		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SendCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearContextCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> BackToLiveCommand { get; }

		// ── Infrastructure ────────────────────────────────────────────────────

		[Reactive] public bool IsLocalProvider { get; private set; }
		[Reactive] public string ProviderModelDisplay { get; private set; } = "";
		[Reactive] public string InputWatermark { get; private set; } = "Ask AI… (Enter to send, Shift+Enter for newline)";

		private readonly AiTools _tools;
		private readonly List<JsonObject> _history = new();
		private readonly Queue<string> _pendingBreakContext = new();
		private readonly ObservableCollection<ChatEntry> _archivedSessionMessages = new();
		private CancellationTokenSource? _cts;
		private string _historyPath = "";
		private string _contextText = "";  // loaded from context file

		public AiCompanionConfig Config => ConfigManager.Config.Debug.AiCompanion;

		// ── Constructor ───────────────────────────────────────────────────────

		public AiCompanionViewModel()
		{
			ActiveMessages = Messages;

			_tools = new AiTools();
			_tools.OnToolLog = msg => Dispatcher.UIThread.Post(() => AddToolLog(msg));
			_tools.GetAndClearPendingBreaks = () => {
				var list = _pendingBreakContext.ToList();
				_pendingBreakContext.Clear();
				return list;
			};

			var canSend = this.WhenAnyValue(
				x => x.IsBusy,
				busy => !busy);

			SendCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSend);
			CancelCommand = ReactiveCommand.Create(() => _cts?.Cancel());
			ClearCommand = ReactiveCommand.Create(ClearChat);
			ClearContextCommand = ReactiveCommand.Create(ClearContextFile);
			BackToLiveCommand = ReactiveCommand.Create(BackToLive);

			ArchivedSessions.CollectionChanged += (_, _) => HasArchivedSessions = ArchivedSessions.Count > 0;

			// Mirror provider flag and update display text when provider or model changes
			AddDisposable(Config.WhenAnyValue(x => x.Provider, x => x.Model, (p, m) => (p, m))
				.Subscribe(t => {
					IsLocalProvider = t.p == AiProvider.OpenAiCompatible;
					string providerLabel = t.p == AiProvider.OpenAiCompatible ? "Custom AI" : "Claude";
					ProviderModelDisplay = $"{providerLabel} · {t.m}";
					InputWatermark = "Ask AI… (Enter to send, Shift+Enter for newline)";
				}));

			LoadHistory();
		}

		// ── Public API ────────────────────────────────────────────────────────

		/// <summary>
		/// Called when the emulator hits a user-set breakpoint.
		/// Skips pauses, steps, and any non-user-triggered break source.
		/// If already busy, queues the context snapshot for the AI to read via get_pending_breakpoints.
		/// </summary>
		public void OnBreakpointHit(BreakEvent evt)
		{
			// Only react to real user-set breakpoints, not pause/step/NMI/etc.
			if(evt.Source != BreakSource.Breakpoint) return;

			uint addr = (uint)evt.Operation.Address;
			string cpu = evt.SourceCpu.ToString();

			// Capture CPU state + disassembly immediately, before any async work runs.
			string context = _tools.BuildBreakContext(evt.SourceCpu);
			string header  = $"Breakpoint #{evt.BreakpointId} hit: {cpu} at ${addr:X6}";
			string full    = $"{header}\n{context}";

			if(IsBusy) {
				// Queue for the AI to retrieve via get_pending_breakpoints
				_pendingBreakContext.Enqueue(full);
				return;
			}

			string prompt = $"[{header}] The emulator has paused. " +
			                $"Identify what code is running here and apply labels/comments if unannotated.\n\n" +
			                context;

			StatusText = $"Breakpoint hit at ${addr:X6} ({cpu})";
			_ = RunTurnAsync(prompt);
		}

		public void OnGameLoaded()
		{
			BackToLive();
			_tools.Reset();
			_cts?.Cancel();

			// Save previous ROM's session using its cached path (GetHistoryPath() now returns the new ROM's path)
			SaveHistoryToPath(_historyPath);

			_history.Clear();
			Messages.Clear();
			ToolCallLog.Clear();
			_pendingBreakContext.Clear();

			// Load history for newly loaded ROM
			LoadHistory();
		}

		// ── Context File ──────────────────────────────────────────────────────

		/// <summary>
		/// Resolves the auto-discovery path: {romDir}/{romName}.context.txt
		/// Returns "" if no ROM is loaded.
		/// </summary>
		private static string GetAutoContextPath()
		{
			RomInfo info = EmuApi.GetRomInfo();
			string romPath = info.RomPath;
			if(string.IsNullOrEmpty(romPath)) return "";
			return Path.ChangeExtension(romPath, ".context.txt");
		}

		public void LoadContextFile(string path)
		{
			try {
				_contextText = File.ReadAllText(path);
				Config.ContextFilePath = path;
				ContextStatusText = $"ctx: {Path.GetFileName(path)}";
				AddSystemMessage($"Context file loaded: {Path.GetFileName(path)} ({_contextText.Length} chars)");
			} catch(Exception ex) {
				AddSystemMessage($"Could not load context file: {ex.Message}");
			}
		}

		public void ClearContextFile()
		{
			_contextText = "";
			Config.ContextFilePath = "";
			ContextStatusText = "no context";
			AddSystemMessage("Context file cleared.");
		}

		private void TryAutoLoadContext()
		{
			// 1. Use the configured path if set and file exists
			string configured = Config.ContextFilePath;
			if(!string.IsNullOrEmpty(configured) && File.Exists(configured)) {
				try {
					_contextText = File.ReadAllText(configured);
					ContextStatusText = $"ctx: {Path.GetFileName(configured)}";
					AddSystemMessage($"Context file loaded: {Path.GetFileName(configured)} ({_contextText.Length} chars)");
					return;
				} catch { }
			}

			// 2. Fall back to auto-discovery: {romName}.context.txt next to the ROM
			string auto = GetAutoContextPath();
			if(!string.IsNullOrEmpty(auto) && File.Exists(auto)) {
				try {
					_contextText = File.ReadAllText(auto);
					ContextStatusText = $"ctx: {Path.GetFileName(auto)}";
					AddSystemMessage($"Context file auto-loaded: {Path.GetFileName(auto)} ({_contextText.Length} chars)");
					return;
				} catch { }
			}

			_contextText = "";
			ContextStatusText = "no context";
		}

		// ── History Persistence ───────────────────────────────────────────────

		private static string GetHistoryPath()
		{
			RomInfo info = EmuApi.GetRomInfo();
			if(string.IsNullOrEmpty(info.GetRomName())) return "";
			return Path.Combine(ConfigManager.DebuggerFolder, info.GetRomName() + ".ai.json");
		}

		private void SaveCurrentHistory() => SaveHistoryToPath(GetHistoryPath());

		private void SaveHistoryToPath(string path)
		{
			try {
				if(string.IsNullOrEmpty(path)) return;

				var messagesArray = new JsonArray();
				foreach(var entry in Messages) {
					if(entry.Kind == ChatEntry.EntryKind.System) continue;
					messagesArray.Add((JsonNode)new JsonObject {
						["kind"] = (int)entry.Kind,
						["text"] = entry.Text,
						["ts"] = entry.Timestamp.ToString("o")
					});
				}

				var historyArray = new JsonArray();
				foreach(var msg in _history)
					historyArray.Add((JsonNode)msg.DeepClone());

				var root = new JsonObject {
					["messages"] = messagesArray,
					["history"] = historyArray
				};

				File.WriteAllText(path, root.ToJsonString());
			} catch {
				// Non-fatal — silently ignore save failures
			}
		}

		private void LoadHistory()
		{
			TryAutoLoadContext();
			try {
				_historyPath = GetHistoryPath();
				if(string.IsNullOrEmpty(_historyPath) || !File.Exists(_historyPath)) {
					AddSystemMessage("AI Companion ready. No previous chat history for this ROM.");
					return;
				}
				string path = _historyPath;

				var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
				if(root == null) { AddSystemMessage("AI Companion ready."); return; }

				if(root["messages"] is JsonArray msgs) {
					foreach(var node in msgs) {
						if(node is not JsonObject obj) continue;
						var kind = (ChatEntry.EntryKind)(obj["kind"]?.GetValue<int>() ?? 0);
						string text = obj["text"]?.GetValue<string>() ?? "";
						DateTime ts = DateTime.TryParse(obj["ts"]?.GetValue<string>(), out var t) ? t : DateTime.Now;
						var entry = new ChatEntry { Kind = kind, Text = text, Timestamp = ts };
						if(kind == ChatEntry.EntryKind.ToolStatus)
							ToolCallLog.Add(entry);
						else
							Messages.Add(entry);
					}
				}

				if(root["history"] is JsonArray hist) {
					foreach(var node in hist) {
						if(node is JsonObject obj)
							_history.Add(obj.DeepClone() as JsonObject ?? new JsonObject());
					}
				}

				AddSystemMessage($"Chat history restored ({Messages.Count} entries).");
			} catch {
				AddSystemMessage("AI Companion ready. (Could not load previous history.)");
			}
			LoadArchivedSessions();
		}

		// ── Private ───────────────────────────────────────────────────────────

		private async Task SendMessageAsync()
		{
			string text = InputText.Trim();
			if(string.IsNullOrEmpty(text)) return;
			InputText = "";

			await RunTurnAsync(text);
		}

		private async Task RunTurnAsync(string userMessage)
		{
			bool isLocal = Config.Provider == AiProvider.OpenAiCompatible;
			if(!isLocal && string.IsNullOrEmpty(Config.ApiKey)) {
				Dispatcher.UIThread.Post(() => AddEntry(ChatEntry.EntryKind.Error, "API key not set. Enter your Anthropic API key in the Settings panel."));
				return;
			}

			var cts = new CancellationTokenSource();
			_cts = cts;

			// Capture config values on calling thread before any await
			string apiKey = isLocal ? Config.LocalApiKey : Config.ApiKey;
			string model = Config.Model;
			int maxTokens = Config.MaxTokens;
			int maxHistoryTurns = Config.MaxHistoryTurns;
			int maxToolCallsPerTurn = Config.MaxToolCallsPerTurn;
			string systemPrompt = BuildSystemPrompt();

			// Add user message to history (API state — thread-safe list)
			_history.Add(new JsonObject {
				["role"] = "user",
				["content"] = new JsonArray { (JsonNode)new JsonObject { ["type"] = "text", ["text"] = userMessage } }
			});

			// All UI mutations on UI thread
			ChatEntry assistantEntry = new ChatEntry { Kind = ChatEntry.EntryKind.Assistant, IsStreaming = true };
			await Dispatcher.UIThread.InvokeAsync(() => {
				IsBusy = true;
				StatusText = "AI is thinking...";
				AddEntry(ChatEntry.EntryKind.User, userMessage);
				Messages.Add(assistantEntry);
			});

			IAiClient client = isLocal
				? new OpenAiCompatibleClient(Config.LocalApiEndpoint)
				: new ClaudeClient();

			// Proactively compact history before it hits the token limit.
			// This summarizes dropped turns rather than silently discarding them.
			await TryCompactHistoryAsync(client, apiKey, model, maxHistoryTurns, cts.Token);

			try {
				await client.RunTurnAsync(
					apiKey: apiKey,
					model: model,
					maxTokens: maxTokens,
					maxHistoryTurns: maxHistoryTurns,
					maxToolCallsPerTurn: maxToolCallsPerTurn,
					systemPrompt: systemPrompt,
					messages: _history,
					tools: _tools.GetDefinitions(),
					toolExecutor: (name, input) => _tools.ExecuteAsync(name, input),
					onTextDelta: delta => {
						Dispatcher.UIThread.Post(() => assistantEntry.AppendText(delta));
					},
					onToolStatus: status => {
						// Only log non-tool-call status messages here (e.g. limit reached);
						// per-tool detail is logged via AiTools.OnToolLog.
						if(!status.StartsWith("[tool:"))
							Dispatcher.UIThread.Post(() => AddToolLog(status));
					},
					ct: cts.Token);

				Dispatcher.UIThread.Post(() => {
					assistantEntry.IsStreaming = false;
					StatusText = "Ready";
				});
			} catch(OperationCanceledException) {
				Dispatcher.UIThread.Post(() => {
					assistantEntry.IsStreaming = false;
					assistantEntry.AppendText("\n[Cancelled]");
					StatusText = "Cancelled";
				});
			} catch(Exception ex) {
				string errorMsg = ex.Message;
				Dispatcher.UIThread.Post(() => {
					assistantEntry.IsStreaming = false;
					if(string.IsNullOrEmpty(assistantEntry.Text))
						Messages.Remove(assistantEntry);
					AddEntry(ChatEntry.EntryKind.Error, $"Error: {errorMsg}");
					StatusText = "Error";
				});
			} finally {
				Dispatcher.UIThread.Post(() => {
					IsBusy = false;
					cts.Dispose();
					_cts = null;
				});
			}
		}

		/// <summary>
		/// If TrimHistory would drop any turns from _history, summarizes the oldest portion
		/// via a quick sub-request to the AI and replaces those messages with a compact
		/// summary exchange. Falls back to TrimHistory's normal behavior on any error.
		/// </summary>
		private async Task TryCompactHistoryAsync(
			IAiClient client, string apiKey, string model, int maxTurns, CancellationToken ct)
		{
			var trimmed = AiMessageHelpers.TrimHistory(_history, maxTurns);
			if(trimmed.Count >= _history.Count) return;  // history fits — nothing to compact

			int dropCount = _history.Count - trimmed.Count;
			var oldPortion = _history.GetRange(0, dropCount);
			if(oldPortion.Count == 0) return;

			Dispatcher.UIThread.Post(() => StatusText = "Compacting conversation history...");

			string prompt = AiMessageHelpers.BuildCompactionPrompt(oldPortion);
			var tempMessages = new List<JsonObject> {
				new JsonObject {
					["role"] = "user",
					["content"] = new JsonArray {
						(JsonNode)new JsonObject { ["type"] = "text", ["text"] = prompt }
					}
				}
			};

			string summary = "";
			try {
				await client.RunTurnAsync(
					apiKey: apiKey,
					model: model,
					maxTokens: 1500,
					maxHistoryTurns: 1,
					maxToolCallsPerTurn: 0,
					systemPrompt: "You are a conversation summarizer. Be concise but preserve every specific detail: exact label names, addresses, findings, and any outstanding tasks.",
					messages: tempMessages,
					tools: new List<JsonObject>(),
					toolExecutor: (_, _) => Task.FromResult(""),
					onTextDelta: delta => summary += delta,
					onToolStatus: _ => { },
					ct: ct);
			} catch(OperationCanceledException) {
				throw;  // propagate cancellation — do not swallow
			} catch {
				// Non-fatal: silently fall back to TrimHistory's truncation
				Dispatcher.UIThread.Post(() => StatusText = "AI is thinking...");
				return;
			}

			if(string.IsNullOrWhiteSpace(summary)) return;

			// Replace the old portion with a synthetic two-message summary exchange
			_history.RemoveRange(0, dropCount);
			_history.Insert(0, new JsonObject {
				["role"] = "assistant",
				["content"] = new JsonArray {
					(JsonNode)new JsonObject {
						["type"] = "text",
						["text"] = $"[Summary of earlier conversation]\n\n{summary}"
					}
				}
			});
			_history.Insert(0, new JsonObject {
				["role"] = "user",
				["content"] = new JsonArray {
					(JsonNode)new JsonObject {
						["type"] = "text",
						["text"] = "[Earlier context has been compacted. The following summary covers what we discussed before this point.]"
					}
				}
			});

			Dispatcher.UIThread.Post(() => {
				AddSystemMessage($"History compacted: {dropCount} older messages summarized to save context space.");
				StatusText = "AI is thinking...";
			});
		}

		// ── System prompt helpers ─────────────────────────────────────────────

		/// <summary>
		/// Probes the emulator's address translator to determine ROM map mode.
		/// Returns "LoROM", "HiROM", "ExHiROM", or "Unknown".
		/// </summary>
		private static string DetectSnesMapMode(int romSize)
		{
			if(romSize <= 0) return "Unknown";
			try {
				// ROM offset 0 translates to $808000 on LoROM, $C00000 on HiROM/ExHiROM
				var rel = DebugApi.GetRelativeAddress(
					new AddressInfo { Address = 0, Type = MemoryType.SnesPrgRom }, CpuType.Snes);
				if(rel.Address < 0) return "Unknown";
				uint bank = (uint)rel.Address >> 16;
				if(bank == 0x80) return "LoROM";
				if(bank >= 0xC0) return romSize > 4 * 1024 * 1024 ? "ExHiROM" : "HiROM";
				// Some games mirror differently — fall back to size heuristic
				return romSize > 4 * 1024 * 1024 ? "ExHiROM" : romSize > 2 * 1024 * 1024 ? "HiROM" : "LoROM";
			} catch {
				return romSize > 2 * 1024 * 1024 ? "HiROM" : "LoROM";
			}
		}

		/// <summary>Builds a comma-separated list of detected coprocessors from the ROM's CpuType set.</summary>
		private static string BuildCoprocessorList(RomInfo info)
		{
			var parts = new System.Collections.Generic.List<string>();
			foreach(var cpu in info.CpuTypes) {
				string? label = cpu switch {
					CpuType.Sa1    => "SA-1 (65816 coprocessor)",
					CpuType.Gsu    => "SuperFX / GSU",
					CpuType.NecDsp => "NEC DSP",
					CpuType.Cx4    => "CX4",
					CpuType.St018  => "ST018",
					CpuType.Gameboy => "SGB / Game Boy CPU",
					_ => null
				};
				if(label != null) parts.Add(label);
			}
			return parts.Count > 0 ? string.Join(", ", parts) : "None";
		}

		private string BuildSystemPrompt()
		{
			// ── Gather ROM facts ──────────────────────────────────────────────────
			RomInfo romInfo   = EmuApi.GetRomInfo();
			bool romLoaded    = romInfo.Format != RomFormat.Unknown && !string.IsNullOrEmpty(romInfo.GetRomName());
			int  romSize      = DebugApi.GetMemorySize(MemoryType.SnesPrgRom);
			string mapMode    = DetectSnesMapMode(romSize);
			string coprocList = BuildCoprocessorList(romInfo);
			string romName    = romLoaded ? romInfo.GetRomName() : "(none)";
			bool hasSa1       = romInfo.CpuTypes.Contains(CpuType.Sa1);
			bool hasGsu       = romInfo.CpuTypes.Contains(CpuType.Gsu);

			// Map-mode specific address layout text
			string mapLayout = mapMode switch {
				"LoROM" =>
					"LoROM layout:\n" +
					"  Banks $00–$3F / $80–$BF : offsets $0000–$7FFF = system bus; offsets $8000–$FFFF = ROM\n" +
					"  Banks $40–$6F / $C0–$EF : full 64KB slices = ROM (large LoROM only)\n" +
					"  ROM offset formula      : ((bank & $7F) << 15) | (addr & $7FFF)\n" +
					"  SNES CPU address $80:8000 = ROM offset $000000 (bank 0 of ROM)",
				"HiROM" =>
					"HiROM layout:\n" +
					"  Banks $C0–$FF : full 64KB = ROM (primary window)\n" +
					"  Banks $40–$7D : full 64KB = ROM (same, no system-bus overlay)\n" +
					"  Banks $00–$3F / $80–$BF : offsets $8000–$FFFF = ROM (mirrored from $C0–$FF)\n" +
					"  ROM offset formula      : ((bank & $3F) << 16) | addr\n" +
					"  SNES CPU address $C0:0000 = ROM offset $000000",
				"ExHiROM" =>
					"ExHiROM layout (>4 MB HiROM):\n" +
					"  Banks $C0–$FF : ROM offsets $000000–$3FFFFF (first 4 MB)\n" +
					"  Banks $40–$7D : ROM offsets $400000–$7DFFFF (upper 4 MB)\n" +
					"  Banks $80–$BF : offsets $8000–$FFFF mirror ROM offsets $400000+\n" +
					"  Banks $00–$3F : offsets $8000–$FFFF mirror ROM offsets $400000+",
				_ =>
					"Memory map: Unknown/unusual — probe with read_memory or get_disassembly to determine layout."
			};

			// ── Assemble the prompt ───────────────────────────────────────────────
			var sb = new System.Text.StringBuilder();

			sb.AppendLine(
@"You are an experienced SNES ROM hacker and reverse engineer integrated into the Mesen2-Diz emulator/debugger. You think like a disassembly veteran: you understand how SNES games are structured, recognize common code patterns instantly, know how data is laid out, and drive annotation coverage systematically from zero toward a complete, accurate map.

Your primary mission: build a precise, complete annotation of the ROM — correct label types, meaningful names, useful comments — and guide the user through the gameplay required to expose unreached code.");

			// ── Loaded ROM ────────────────────────────────────────────────────────
			sb.AppendLine();
			sb.AppendLine("## Loaded ROM");
			if(!romLoaded || romSize <= 0) {
				sb.AppendLine("No ROM is currently loaded. Most tools will be unavailable until a ROM is loaded.");
			} else {
				sb.AppendLine($"- Name         : {romName}");
				sb.AppendLine($"- Format       : {romInfo.Format}");
				sb.AppendLine($"- Size         : {romSize / 1024} KB ({romSize:N0} bytes)");
				sb.AppendLine($"- Memory map   : {mapMode}");
				sb.AppendLine($"- Coprocessors : {coprocList}");
				if(hasSa1)
					sb.AppendLine("  ⚠ SA-1: The SA-1 is a second 65816 running from the same ROM. It has its own PC, registers, and BWRAM. The current tools target the main SNES CPU only; SA-1 code will show as CDL=0 until the SA-1 runs it.");
				if(hasGsu)
					sb.AppendLine("  ⚠ SuperFX/GSU: GSU code is stored in ROM but executed by the GSU chip. Main CPU code sets up GSU parameters and calls it. GSU instructions are NOT 65816; disassembly of GSU banks will look wrong — annotate them as data.");
			}

			// ── 65816 CPU Registers ───────────────────────────────────────────────
			sb.AppendLine();
			sb.AppendLine(
@"## 65816 CPU Registers
The 65816 has two operating modes set by the Emulation (E) bit (not in PS; set via XCE instruction):
  Native mode  (E=0): full 65816, 16-bit capable, used by nearly all SNES games after reset init
  Emulation mode (E=1): 6502 compatibility mode, 8-bit only, rarely used intentionally

Registers (all values as seen in get_cpu_state):
  A   Accumulator. 8-bit when PS.M=1 (MemoryMode8); 16-bit when PS.M=0.
      In 8-bit mode the upper byte is the hidden B register, preserved across SEP/REP.
  X   Index register X. 8-bit when PS.X=1 (IndexMode8); 16-bit when PS.X=0.
      In 8-bit mode the upper byte is forced to 0 on switch to 8-bit.
  Y   Index register Y. Same width rules as X.
  SP  Stack pointer. 16-bit in native mode. Stack lives at $00:0100–$00:01FF (typically).
      Stack grows downward. JSR/JSL/PHP/PHA/PHX/PHY/PHD/PHB/PHK all push to the stack.
  D   Direct page register. Added to all direct-page (DP) addressing mode operands.
      Normally $0000 (bank 0). If D is non-zero, DP addressing hits arbitrary WRAM.
  K   Program bank register (PBR). The bank byte of the current PC.
      Changed by JSL/JML/RTL/RTI but NOT by JSR/JMP/RTS (those stay in the same bank).
  DBR Data bank register (DB/DBR). Default bank for absolute addressing (LDA $XXXX, STA $XXXX).
      PHB/PLB push/pull this. Set to ROM bank at subroutine entry (PHB; PEA bank<<8; PLB).
  PC  Program counter (16-bit offset within bank K). Full address = K:PC.
  PS  Processor status byte. Individual flags:
        N (bit7) Negative   — set if result bit 7 (8-bit) or bit 15 (16-bit) is 1
        V (bit6) Overflow   — set on signed arithmetic overflow
        M (bit5) MemoryMode8 — 1 = A is 8-bit; 0 = A is 16-bit  (REP/SEP #$20)
        X (bit4) IndexMode8  — 1 = X/Y are 8-bit; 0 = X/Y are 16-bit  (REP/SEP #$10)
        D (bit3) Decimal    — BCD mode (rarely used on SNES; CLD usually kept clear)
        I (bit2) IRQ Disable — 1 = hardware IRQ masked; 0 = IRQ enabled  (SEI/CLI)
        Z (bit1) Zero       — set if result == 0
        C (bit0) Carry      — set on unsigned overflow or after comparison

Key width-switching instructions:
  REP #$20  → M=0 → A becomes 16-bit    SEP #$20  → M=1 → A becomes 8-bit
  REP #$10  → X=0 → X/Y become 16-bit   SEP #$10  → X=1 → X/Y become 8-bit
  REP #$30  → both A and X/Y 16-bit      SEP #$30  → both 8-bit
  XCE       → swap E and C bits (switches native/emulation mode)

CDL mode flags in disassembly:
  X8 flag on a byte = X/IX was 1 (8-bit index) when that instruction was executed
  M8 flag on a byte = M was 1 (8-bit accumulator) when that instruction was executed
  Use these to know the register widths at any given code byte without running the code.");

			// ── Memory Map ────────────────────────────────────────────────────────
			sb.AppendLine();
			sb.AppendLine("## SNES Memory Map");
			sb.AppendLine(mapLayout);
			sb.AppendLine(
@"
System bus regions (all map modes):
  $00–$3F : $0000–$1FFF  Work RAM mirror (first 8KB of $7E0000)
  $00–$3F : $2100–$213F  PPU registers (write-only except status regs)
  $00–$3F : $2140–$2143  APU / SPC700 communication ports (I/O with the SPC700)
  $00–$3F : $2180–$2183  WRAM access port ($2180 data, $2181–$2183 address)
  $00–$3F : $4016–$4017  Joypad serial I/O
  $00–$3F : $4200–$420F  Interrupt/DMA enable, timer registers, WRIO, RDNMI, TIMEUP
  $00–$3F : $4210        RDNMI — NMI flag (read clears it)
  $00–$3F : $4211        TIMEUP — IRQ flag (read clears it)
  $00–$3F : $4212        HVBJOY — H/V blank and joypad status
  $00–$3F : $4214–$4217  Hardware multiply/divide result registers
  $00–$3F : $4300–$437F  DMA registers (8 channels × $10 bytes each)
  $7E:0000–$7FFFFF       Work RAM (128 KB total; bank $7F is the upper half)
  $7E:0000–$00FF         Zero page equivalent (direct page default target)
  $7E:0100–$01FF         Default stack area

Key PPU registers:
  $2100 INIDISP  Screen brightness / forced blank
  $2101 OBSEL    OBJ/sprite character base + size
  $2102–$2104    OAM address + data port
  $2105 BGMODE   BG mode (0–7) + BG3 priority
  $210B–$210C    BG char data base addresses
  $2115–$2119    VRAM address + data ports
  $211A–$2120    Mode 7 parameters
  $2121–$2122    CGRAM (palette) address + data port
  $213F STAT78   PPU2 status (interlace, field, external sync)

Key DMA channel registers (channel N at base $4300 + N*$10):
  $43N0 DMAPn   DMA control (direction, step, transfer unit)
  $43N1 BBADn   PPU bus address (B-bus; e.g. $18 for VRAM data write $2118)
  $43N2–$43N4   ABANKn/A1Tn/A2Tn — source address (bank:address)
  $43N5–$43N6   DASn — transfer length in bytes
  $420B MDMAEN  Start DMA on selected channels (write a bitmask)
  $420C HDMAEN  Enable HDMA channels (set before V-blank)

Hardware interrupt vectors (native mode, all map modes):
  $00:FFEA–$00:FFEB  NMI vector   (fires once per frame at V-blank start)
  $00:FFEE–$00:FFEF  IRQ vector   (H/V timer or coprocessor IRQ)
  $00:FFFC–$00:FFFD  RESET vector (CPU starts here after power-on/reset)
  $00:FFE4–$00:FFE5  COP vector
  $00:FFE6–$00:FFE7  BRK vector
  Emulation-mode vectors at $00:FFF[A/C/E/4/6/8]");

			// ── System Quirks ─────────────────────────────────────────────────────
			sb.AppendLine();
			sb.AppendLine(
@"## Common SNES / 65816 Quirks
1. First instruction after RESET always runs in emulation (6502) mode. Games switch to native mode immediately: CLC / XCE.
2. After XCE to native mode, A/X/Y are still 8-bit (M=1, X=1). A typical init sequence: REP #$30 (both 16-bit), then set up D, SP, DBR.
3. Stack pointer: after RESET the SP is $01FF. Games usually set: LDX #$01FF / TXS (or REP #$30 / LDX #$1FF / TXS).
4. Direct page (D register): DP addressing adds D to the operand. If D=$0000, LDA $20 reads WRAM $7E0020. If D=$1000, LDA $20 reads WRAM $7E1020.
5. Data bank (DBR): absolute addressing (non-DP, non-long) uses DBR as the bank. Always check DBR when reading 16-bit addresses to know which bank they resolve to.
6. Long addressing (LDA $BBAAAA, JSL $BBAAAA) ignores DBR entirely — the full 24-bit address is in the instruction.
7. Indexed addressing overflow: LDA $8000,X with X=$1000 and DBR=$80 reads from bank $80 address $9000, but wrapping behavior at $FFFF depends on DP vs absolute.
8. DMA during active display will corrupt graphics — games always run DMA inside V-blank (NMI handler or forced-blank window).
9. V-blank / NMI timing: NMI fires when scanline 225 starts. Games must complete all DMA/OAM/CGRAM uploads before rasterization resumes at scanline 0.
10. HDMA runs once per scanline for each enabled channel; it overwrites the same PPU registers as regular DMA but on a per-scanline basis (used for raster effects, scrolling, windowing).
11. The SPC700 is a completely separate 6502-like CPU running SPC700 code from its own 64KB address space. Communication happens through the 4 APU ports at $2140–$2143. SPC code is uploaded to SPC700 RAM at boot; it is NOT in the ROM address space visible to the main CPU.
12. OAM (sprite table): 544 bytes at the internal OAM address ($2102/$2103). First 512 bytes = 128 sprites × 4 bytes (X/Y/tile/attr). Last 32 bytes = size/X-high-bit extension for each sprite group.
13. CGRAM: 256 palette entries × 2 bytes (BGR555 format). Palettes 0–7 = background, 8–15 = sprites. Write via $2121 (address) and $2122 (data, low then high byte).
14. Mode 7: single BG layer, 8-bit tile indices, 8×8 tiles, each tile has its own palette entry, affine transform applied per scanline via $211B–$2120. Mode 7 games often use bank $7E for tile map and $7F or a ROM bank for tile data.
15. FastROM ($420D bit 0): enables fast (3.58MHz) ROM access for banks $80–$FF. SlowROM banks $00–$3F / $40–$7F run at 2.68MHz. A game must set EnableFastRom=1 ($4200 family) to benefit.");

			// ── CDL transience reminder ───────────────────────────────────────────
			sb.AppendLine();
			sb.AppendLine(
@"## Critical Rule: CDL Coverage is Session-Transient
CDL (Code/Data Log) figures reported by get_rom_map and get_annotation_summary reflect ONLY what has been executed in the current debugger session. They reset to zero when the session starts and grow as the game runs under the debugger.

NEVER treat CDL coverage percentages as persistent facts about the ROM. NEVER include CDL coverage figures in any turn summary you write — the values will be stale and misleading in future turns. Call get_rom_map again when you need current coverage.");

			// ── Available Tools ───────────────────────────────────────────────────
			sb.AppendLine();
			sb.AppendLine(
@"## Available Tools — complete catalog

Every tool response automatically appends the current CPU state and a disassembly snippet at PC so you rarely need a separate get_cpu_state call.

### ROM overview & annotation
get_rom_map
  Returns: CDL coverage stats, full label list with sizes, contiguous unreached (CDL=0) ranges.
  When: Start of every session. Any time you need a broad view. Supports `bank` filter and `max_ranges`.
  Never replace this with separate get_labels + get_annotation_summary calls.

get_annotation_summary
  Returns: aggregate CDL counts (code bytes, data bytes, function count, unannotated targets).
  When: Quick coverage check without needing the full label list.

get_unlabeled_functions
  Returns: all CDL-identified SubEntryPoint addresses that are missing a label, comment, or both.
  When: To find the next batch of unannotated functions to work on. Supports `filter` and `bank`.
  Filters: 'either' (default), 'no_label', 'no_comment', 'both'.

get_cdl_functions_paged
  Returns: paged list of CDL functions in one bank with their current label/comment.
  When: Systematically enumerate every function in a bank page-by-page.
  Required param: `bank`. Optional: `page`, `page_size`, `filter`.

get_cdl_data
  Returns: CDL flag per byte for a ROM offset range (Code/Data/JumpTarget/SubEntry/X8/M8).
  When: Inspect CDL state for a specific range, especially before set_data_type.
  Param: `rom_offset` (integer ROM file offset, not SNES address), `length`.

set_data_type
  Action: Marks an address range with a CDL type flag.
  When: Explicitly declare what type of data an address range contains — e.g., mark a known
        data table as 'data' before execution has touched it, or correct a misidentified region.
  Params: `address` (SNES CPU), `length`, `type` ('code'|'data'|'jump_target'|'sub_entry'|'none').

### Disassembly & memory
get_disassembly
  Returns: formatted disassembly lines with addresses, byte codes, mnemonics, existing labels/comments.
  When: Inspect code at any address. Read 20–40 lines before naming anything.
  Param: `address` (SNES CPU address), optional `line_count` (default 32, max 256).

read_memory
  Returns: hex + ASCII dump of a memory region.
  When: Inspect data tables, pointer tables, strings, RAM state. Static ROM reads are cached.
  Params: `address`, `length`, optional `memory_type` (cpu|prg_rom|work_ram|save_ram|vram|oam|cgram).

write_memory
  Action: Write bytes to emulated memory. Immediate effect even while running.
  When: Patch code/data for testing, set RAM variables, force game state.
  Params: `address`, `data` (space-separated hex pairs e.g. 'A9 01 60'), `memory_type`.

### Labels & annotations
get_labels
  Returns: all defined labels (address, memory type, name, comment).
  When: Need the complete label list. Prefer get_rom_map for a combined view.

get_label_at
  Returns: label name and comment at one SNES address.
  When: Check whether a specific address is already labeled before setting one.

set_label
  Action: Set name and/or comment at one SNES address.
  When: Single label update. For multiple addresses always use set_labels instead.
  Param: `address`, `name` (empty = keep existing), `comment`.

set_labels
  Action: Batch-set any number of labels in one call.
  When: ALWAYS prefer this over repeated set_label calls after analyzing a function or bank.
  Each entry: {address, name, comment}.

delete_label
  Action: Remove the label at a SNES address.
  When: Correct a wrong label before setting the right one, or clean up auto-generated stubs.

### CPU state & registers
get_cpu_state
  Returns: all 65816 registers (A, X, Y, SP, D, K, DBR, PC) and all PS flags.
  When: Check exact register values when paused. (Note: all other tools also append a CPU state
        summary automatically — only call this when you need the definitive canonical state.)

set_cpu_registers
  Action: Set one or more registers. Emulation MUST be paused first.
  When: Force a specific execution path for testing, patch a register value.
  All fields optional: a, x, y, sp, d, pc, k, dbr, ps.

### Emulation control
pause_emulation   — Pause at the next convenient instruction boundary.
resume_emulation  — Resume from paused/breakpoint state.
reset_game        — Soft reset (SNES reset button) or hard reset (power cycle).
                    Param: `type` = 'soft' (default) or 'hard'.

### Stepping (all require emulation to be paused first)
step_into         — Execute one instruction; follows JSR/JSL into subroutines.
step_over         — Execute one instruction; treats JSR/JSL as a single step.
step_out          — Run until RTS/RTL/RTI of the current subroutine, then pause.
step_back         — Undo one instruction (requires step-back feature enabled).
step_back_scanline — Rewind to start of the previous PPU scanline.
step_back_frame   — Rewind to start of the previous video frame.
run_cpu_cycle     — Advance by exactly one CPU master clock cycle.
run_ppu_cycle     — Advance by exactly one PPU pixel clock dot.
run_one_frame     — Advance by one complete video frame (~262 scanlines NTSC).
run_to_nmi        — Run until next NMI (V-blank start, once per frame ~scanline 225).
run_to_irq        — Run until next hardware IRQ.
run_to_scanline   — Run to a specific scanline number (0–261). Param: `scanline`.
break_in          — Advance N steps of a specified type then pause. Params: `count`, `type`
                    (instruction|cpu_cycle|ppu_cycle|ppu_scanline|ppu_frame).

### Breakpoints
list_breakpoints
  Returns: all active breakpoints with address, type, condition, enabled state.

set_breakpoint
  Action: Add a breakpoint. Emulation pauses when hit AND condition (if any) is true.
  Params: `address` (required), `end_address` (optional, for a range), `break_on`, `memory_type`, `condition`.
  break_on values: exec (default), read, write, read_write, all.
  Condition syntax: C-like expression using registers (A X Y SP PC K DBR D), individual flags
    (N V M IX D I Z C), memory reads ([$7E0010] = 1 byte at address, [label] = memory at label),
    operators (== != < > <= >= && || ! + - * / & | ^ ~ << >>), literals (255 $FF %11111111).
    Examples: 'A == $FF'  'X > 0 && [$7E0040] != 0'  'PS & $20' (M flag set).
  Range example: address=$7E0100 end_address=$7E01FF break_on=write → break on any write to that RAM region.

remove_breakpoint
  Action: Remove all breakpoints at an address.
  Params: `address`, optional `memory_type` (default: cpu).

### Watch expressions
add_watch
  Action: Add watch expressions to the debugger watch panel.
  Expression syntax: registers (A X Y SP), flags (N V M IX D I Z C), memory reads ([$300]),
    array display ([$300,16] = 16 bytes), arithmetic (A + [$300]), operators (+ - * / & | ^ ~).
  Format suffix: ', H' = hex 1B  ', H2' = hex 2B  ', S' = signed  ', U' = unsigned  ', B' = binary.
  Examples: 'A, H2'  '[$7E0040], H'  '[$300, 8]'  '([$7E0041]<<8)|[$7E0040], H2'.
  Param: `expressions` = array of expression strings.

get_watches
  Returns: all watches with 0-based index, expression string, and current evaluated value.
  When: Inspect watch values, or to get indexes before calling remove_watch.

remove_watch
  Action: Remove watches by index.
  Param: `indexes` = array of 0-based integers (use get_watches first to confirm indexes).

### Call stack
get_call_stack
  Returns: current subroutine call chain with source, target, and return address for each frame.
  When: Understand the call depth and what routine called the current one. Requires paused emulation.

### Breakpoint events
get_pending_breakpoints
  Returns: all breakpoint events that fired while the AI was busy, with CPU type, full register state,
  and disassembly captured at the moment each break occurred. Clears the queue after reading.
  When: After finishing analysis of a breakpoint hit, call this to check for additional queued breaks.");

			// ── Label taxonomy ────────────────────────────────────────────────────
			sb.AppendLine();
			sb.AppendLine(
@"## Label type taxonomy
Use the most specific correct type. Fixing a wrong type is important — do it with set_labels.

| Type          | When to use                                                        | Naming convention                          |
|---------------|--------------------------------------------------------------------|--------------------------------------------|
| subroutine    | JSL/JSR target (CDL SubEntryPoint) — a callable function          | camelCase verb: initSprites, updatePlayer  |
| branch_target | CDL JumpTarget only, mid-function label                            | .loop .done .next or playerLoop            |
| pointer_table | Table of 2-byte (near) or 3-byte (far/long) pointers              | enemyHandlerTable, stateJumpTable          |
| data_table    | Lookup table of non-pointer values                                 | sinTable, xpThresholdTable                 |
| graphics      | 2bpp/4bpp tile data, sprite sheets, OAM attribute data            | playerSprite, fontTiles                    |
| palette       | BGR555 CGRAM color data                                            | worldPalette, spritePalette0               |
| tilemap       | BG layer tilemap data                                              | titlescreenMap, level1BgMap                |
| animation     | Frame/sequence data, duration tables                               | playerWalkAnim, explosionFrames            |
| collision     | Hitbox definitions, collision-flag tables                          | enemyHitbox, tileCollisionFlags            |
| text          | Dialogue strings, font index sequences                             | introDialogue, menuStrings                 |
| music         | SPC700 song/sequence/pattern data                                  | bgm_overworld, songPatternTable            |
| sfx           | Sound effect data                                                  | sfx_jump, sfxTable                         |
| ai_data       | Enemy behavior scripts, state machine tables                       | bossAiScript, enemyStateTable              |
| vector        | Hardware interrupt/reset vector entries                            | nmiVector, resetVector                     |
| variable      | Named WRAM addresses (game variables)                              | playerHP, cameraX, frameCounter            |");

			// ── Workflow ──────────────────────────────────────────────────────────
			sb.AppendLine();
			sb.AppendLine(
@"## Session startup
1. Call get_rom_map — see coverage, existing labels, unreached ranges.
2. If coverage < 20%: tell the user exactly what in-game actions will expose the main engine.
3. Identify RESET ($00FFFC) and NMI ($00FFEA) vectors as first entry points if unlabeled.
4. Annotate game loop, NMI handler, and DMA routines first — these are the structural skeleton.

## Pointer table identification — priority task
Signs of a pointer table: repeated 2- or 3-byte little-endian values all pointing to valid code;
code preceding it does ASL A (×2) or ADC / multiply by 3 before indexing; a dispatch pattern
like LDA table,X / TAX / JMP (table,X) or JSL (table,X).
To verify: read_memory the bytes → interpret as 16- or 24-bit LE addresses → get_disassembly each
target. If all targets are valid code → it is a pointer table. Label it and label every target as a subroutine.

## Directing gameplay to expose unreached code
You drive coverage. Be specific: name the bank, name what the code likely is, name the in-game action.
  'Bank $84 is unreached — likely level engine. Play through level 1 and trigger one enemy.'
  '$80:8200–9FFF is unreached — typical menu range. Open pause menu, items, and map.'
  'Bank $82 looks like SPC700 data. Trigger 3 different music tracks and 2 sound effects.'
After the user acts, call get_rom_map again to see new CDL coverage, then annotate newly reached code.

## Annotation guidelines
- Read 20–40 disassembly lines before naming anything.
- Name from behavior: spawnEnemy not sub_808240.
- Comments: what does it do, what A/X/Y hold on entry, what are side effects / return values.
- Common recognizable patterns:
    Init         : STZ loops clearing WRAM, then JSL to hardware setup routines
    NMI handler  : PHx push, DMA OAM ($4302/$4305/$420B), PLx pop, RTI
    Game loop    : JSL through a state pointer table indexed by a WRAM game-state variable
    DMA transfer : store source addr → $4302–$4304, dest → $2116/$2118, length → $4305, write $01 → $420B
    Sprite upload: LDA OAM index → $2102, loop writing X/Y/tile/attr bytes to $2104
    DBR setup    : PHB; PEA $XXXX (pushes bank<<8 word); PLB (sets DBR to upper byte)

## Efficiency rules
- Use set_labels for all labels from one analysis batch — never loop set_label.
- Use get_rom_map instead of get_labels + get_annotation_summary.
- Never re-read an address already in this conversation.
- After annotating a batch: report, then STOP — do not start the next batch without user confirmation.

## When to stop
- Code requires specific gameplay to reach: STOP — tell the user exactly what to do and why.
- Task is ambiguous across multiple systems: ask first, act second.
- 5+ tool calls with no clear entry point: STOP and report findings.
- Task complete: STOP and report. No invented follow-on work.

## Communication style
- No preamble. No restating the question. Act, then report.
- Findings as a compact table: address | type | name | reason.
- One sentence per block, not a paragraph.
- When directing gameplay: specific action + why it exposes the target code.");

			// ── User context ──────────────────────────────────────────────────────
			if(!string.IsNullOrWhiteSpace(_contextText))
				sb.AppendLine($"\n## User-supplied context\n\n{_contextText}");

			return sb.ToString();
		}

		private void ClearChat()
		{
			_cts?.Cancel();
			BackToLive();
			ArchiveCurrentSession();
			_tools.Reset();
			_history.Clear();
			Messages.Clear();
			ToolCallLog.Clear();
			_pendingBreakContext.Clear();
			AddSystemMessage("Chat cleared.");
			LoadArchivedSessions();
		}

		public void ViewHistorySession(ChatHistoryEntry entry)
		{
			_archivedSessionMessages.Clear();
			try {
				var root = JsonNode.Parse(File.ReadAllText(entry.FilePath)) as JsonObject;
				if(root?["messages"] is JsonArray msgs) {
					foreach(var node in msgs) {
						if(node is not JsonObject obj) continue;
						var kind = (ChatEntry.EntryKind)(obj["kind"]?.GetValue<int>() ?? 0);
						string text = obj["text"]?.GetValue<string>() ?? "";
						DateTime ts = DateTime.TryParse(obj["ts"]?.GetValue<string>(), out var t) ? t : DateTime.Now;
						_archivedSessionMessages.Add(new ChatEntry { Kind = kind, Text = text, Timestamp = ts });
					}
				}
			} catch { }

			HistorySessionLabel = entry.DisplayText;
			ActiveMessages = _archivedSessionMessages;
			IsViewingHistory = true;
		}

		private void BackToLive()
		{
			if(!IsViewingHistory) return;
			ActiveMessages = Messages;
			IsViewingHistory = false;
			HistorySessionLabel = "";
			_archivedSessionMessages.Clear();
		}

		private void ArchiveCurrentSession()
		{
			// Only archive if there's real content (not just system messages)
			if(!Messages.Any(m => m.Kind != ChatEntry.EntryKind.System)) return;
			if(string.IsNullOrEmpty(_historyPath)) return;

			string dir = Path.GetDirectoryName(_historyPath) ?? "";
			string stem = Path.GetFileNameWithoutExtension(_historyPath);
			string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string archivePath = Path.Combine(dir, $"{stem}.hist.{ts}.json");
			SaveHistoryToPath(archivePath);
		}

		private void LoadArchivedSessions()
		{
			ArchivedSessions.Clear();
			if(string.IsNullOrEmpty(_historyPath)) return;

			string dir = Path.GetDirectoryName(_historyPath) ?? "";
			if(!Directory.Exists(dir)) return;

			string stem = Path.GetFileNameWithoutExtension(_historyPath);
			string prefix = stem + ".hist.";

			foreach(var file in Directory.GetFiles(dir, prefix + "*.json").OrderByDescending(f => f)) {
				string fn = Path.GetFileName(file);
				if(!fn.StartsWith(prefix) || !fn.EndsWith(".json")) continue;
				string tsStr = fn.Substring(prefix.Length, fn.Length - prefix.Length - ".json".Length);
				if(DateTime.TryParseExact(tsStr, "yyyyMMdd_HHmmss", null,
					System.Globalization.DateTimeStyles.None, out var dt))
					ArchivedSessions.Add(new ChatHistoryEntry { Timestamp = dt, FilePath = file });
			}
		}

		private void AddEntry(ChatEntry.EntryKind kind, string text)
		{
			Messages.Add(new ChatEntry { Kind = kind, Text = text });
		}

		private void AddToolLog(string text)
		{
			var entry = new ChatEntry { Kind = ChatEntry.EntryKind.ToolStatus, Text = text };
			ToolCallLog.Add(entry);
		}

		private void AddSystemMessage(string text)
			=> AddEntry(ChatEntry.EntryKind.System, text);

		protected override void DisposeView()
		{
			_cts?.Cancel();
			SaveCurrentHistory();
		}
	}
}
