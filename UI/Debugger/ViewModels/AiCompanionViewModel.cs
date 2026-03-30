using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
		public DateTime Timestamp { get; init; } = DateTime.Now;

		public bool IsUser => Kind == EntryKind.User;
		public bool IsAssistant => Kind == EntryKind.Assistant;
		public bool IsToolStatus => Kind == EntryKind.ToolStatus;
		public bool IsSystem => Kind == EntryKind.System;
		public bool IsError => Kind == EntryKind.Error;

		public string TimestampDisplay => Timestamp.ToString("HH:mm:ss");

		public void AppendText(string delta) => Text += delta;
	}

	public class AiCompanionViewModel : DisposableViewModel
	{
		// ── Observables ───────────────────────────────────────────────────────

		[Reactive] public bool IsBusy { get; private set; }
		[Reactive] public string InputText { get; set; } = "";
		[Reactive] public string StatusText { get; private set; } = "Ready";
		[Reactive] public string ContextStatusText { get; private set; } = "no context";
		[Reactive] public bool ReviewQueueHasItems { get; private set; }
		[Reactive] public bool ChatScrollLocked { get; set; } = true;

		public ObservableCollection<ChatEntry> Messages { get; } = new();
		public ObservableCollection<ChatEntry> ToolCallLog { get; } = new();
		public ObservableCollection<ReviewQueueItem> ReviewQueue { get; }

		// ── Commands ──────────────────────────────────────────────────────────

		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SendCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearContextCommand { get; }
		public ReactiveCommand<ReviewQueueItem, System.Reactive.Unit> AnalyzeQueueItemCommand { get; }

		// ── Infrastructure ────────────────────────────────────────────────────

		[Reactive] public bool IsLocalProvider { get; private set; }
		[Reactive] public string ProviderModelDisplay { get; private set; } = "";

		private readonly AiTools _tools;
		private readonly ExecutionMonitor _monitor;
		private readonly List<JsonObject> _history = new();
		private CancellationTokenSource? _cts;
		private string _historyPath = "";
		private string _contextText = "";  // loaded from context file

		public AiCompanionConfig Config => ConfigManager.Config.Debug.AiCompanion;

		// ── Constructor ───────────────────────────────────────────────────────

		public AiCompanionViewModel()
		{
			_monitor = new ExecutionMonitor();
			_tools = new AiTools { Monitor = _monitor };
			ReviewQueue = _monitor.Queue;

			_monitor.OnNewItem += OnMonitorNewItem;
		_monitor.OnBreakpointHit += OnBreakpointHit;
			ReviewQueue.CollectionChanged += (_, _) => ReviewQueueHasItems = ReviewQueue.Count > 0;

			var canSend = this.WhenAnyValue(
				x => x.IsBusy,
				busy => !busy);

			SendCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSend);
			CancelCommand = ReactiveCommand.Create(() => _cts?.Cancel());
			ClearCommand = ReactiveCommand.Create(ClearChat);
			ClearContextCommand = ReactiveCommand.Create(ClearContextFile);
			AnalyzeQueueItemCommand = ReactiveCommand.CreateFromTask<ReviewQueueItem>(AnalyzeQueueItemAsync, canSend);

			// Mirror provider flag and update display text when provider or model changes
			AddDisposable(Config.WhenAnyValue(x => x.Provider, x => x.Model, (p, m) => (p, m))
				.Subscribe(t => {
					IsLocalProvider = t.p == AiProvider.OpenAiCompatible;
					string providerLabel = t.p == AiProvider.OpenAiCompatible ? "Local" : "Claude";
					ProviderModelDisplay = $"{providerLabel} · {t.m}";
				}));

			// Mirror MonitoringMode config to the ExecutionMonitor
			AddDisposable(Config.WhenAnyValue(x => x.MonitoringMode).Subscribe(mode => {
				if(mode != AiMonitoringMode.Disabled) _monitor.Start();
				else _monitor.Stop();
			}));

			LoadHistory();
		}

		// ── Public API ────────────────────────────────────────────────────────

		public void OnGameLoaded()
		{
			_monitor.Reset();
			_tools.Reset();
			_cts?.Cancel();

			// Save previous ROM's session using its cached path (GetHistoryPath() now returns the new ROM's path)
			SaveHistoryToPath(_historyPath);

			_history.Clear();
			Messages.Clear();
			ToolCallLog.Clear();

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
		}

		// ── Private ───────────────────────────────────────────────────────────

		private async Task SendMessageAsync()
		{
			string text = InputText.Trim();
			if(string.IsNullOrEmpty(text)) return;
			InputText = "";

			await RunTurnAsync(text);
		}

		private async Task AnalyzeQueueItemAsync(ReviewQueueItem item)
		{
			item.IsAnalyzed = true;
			string prompt = $"Please analyze the unannotated {item.FlagsDisplay} at ${item.CpuAddress:X6}. " +
			                $"Examine the disassembly, check what calls this location, identify what it does, " +
			                $"and apply appropriate label and comment annotations.";
			if(!string.IsNullOrEmpty(item.Reason))
				prompt += $"\n\nContext note: {item.Reason}";

			await RunTurnAsync(prompt);
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
			bool showToolCalls = Config.ShowToolCalls;
			string systemPrompt = BuildSystemPrompt();

			// Add user message to history (API state — thread-safe list)
			_history.Add(new JsonObject {
				["role"] = "user",
				["content"] = new JsonArray { (JsonNode)new JsonObject { ["type"] = "text", ["text"] = userMessage } }
			});

			// All UI mutations on UI thread
			ChatEntry assistantEntry = new ChatEntry { Kind = ChatEntry.EntryKind.Assistant, IsStreaming = true };
			string providerName = isLocal ? "Local AI" : "Claude";
			await Dispatcher.UIThread.InvokeAsync(() => {
				IsBusy = true;
				StatusText = $"{providerName} is thinking...";
				AddEntry(ChatEntry.EntryKind.User, userMessage);
				Messages.Add(assistantEntry);
			});

			IAiClient client = isLocal
				? new OpenAiCompatibleClient(Config.LocalApiEndpoint)
				: new ClaudeClient();

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
						if(showToolCalls) {
							Dispatcher.UIThread.Post(() => AddToolStatus(status));
						}
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

		private void OnMonitorNewItem(ReviewQueueItem item)
		{
			Dispatcher.UIThread.Post(() => {
				string msg = $"Monitor: new unannotated {item.FlagsDisplay} at ${item.CpuAddress:X6} added to Review Queue.";
				StatusText = msg;

				if(Config.MonitoringMode == AiMonitoringMode.AutoPause) {
					EmuApi.Pause();
					_ = AnalyzeQueueItemAsync(item);
				}
			});
		}

		private void OnBreakpointHit(BreakEvent evt, uint pc)
		{
			if(Config.MonitoringMode == AiMonitoringMode.Disabled) return;

			Dispatcher.UIThread.Post(() => {
				if(IsBusy) return;  // Already mid-query; don't interrupt

				string prompt = $"[Breakpoint hit at ${pc:X6} (breakpoint #{evt.BreakpointId})] " +
				                $"The emulator has paused at this address. " +
				                $"Read the CPU state and disassembly at ${pc:X6}, identify what code is running here, " +
				                $"and apply labels/comments if the address is unannotated.";

				StatusText = $"Breakpoint hit at ${pc:X6}";
				_ = RunTurnAsync(prompt);
			});
		}

		private string BuildSystemPrompt()
		{
			int romSize = DebugApi.GetMemorySize(MemoryType.SnesPrgRom);
			string romInfo = romSize > 0
				? $"A {romSize / 1024}KB SNES ROM is currently loaded."
				: "No ROM is currently loaded.";

			return $@"You are an experienced SNES ROM hacker and reverse engineer integrated into the Mesen2-Diz emulator/debugger. You think like a disassembly veteran: you know how SNES games are structured, what the common code patterns look like, how data is laid out, and how to systematically drive annotation coverage from zero toward a complete, accurate map of the ROM.

Your primary mission is to build a precise, complete annotation of the ROM — correct label types, meaningful names, useful comments — and to actively guide the user through the gameplay needed to uncover unreached code and data.

{romInfo}

## Session startup
At the start of every new annotation session:
1. Call get_rom_map to see the full picture: CDL coverage, existing labels, and unreached address ranges.
2. Assess coverage. If the ROM is mostly unreached (< 20% CDL), tell the user exactly what to do in-game to expose the main engine: boot the game, get to the title screen, enter a level, walk around, trigger enemies, open menus, etc.
3. Identify the reset vector ($00FFFC) and NMI vector ($00FFEA) as entry points if no labels exist yet.
4. Prioritize annotating the game loop, NMI handler, and DMA routines first — these are the skeleton everything else hangs off.

## Tools available
- get_rom_map — full overview: labels, unreached ranges, CDL stats in one call. Use this at session start and whenever you need a broad view. Never substitute repeated get_labels + get_annotation_summary calls for this.
- get_disassembly — disassembly at an address
- read_memory — read bytes from any memory region
- write_memory — write bytes to any memory region
- get_cdl_data — CDL flags per ROM byte (Code/Data/JumpTarget/SubEntryPoint/X8/M8)
- get_label_at — label and comment at one address
- set_label — set name and/or comment at one address
- set_labels — set many labels in one call (always prefer this over repeated set_label)
- delete_label — remove a label
- get_call_stack — current subroutine call chain (requires paused emulation)
- get_annotation_summary — CDL coverage stats
- add_to_review_queue — queue an address for deferred analysis
- pause_emulation / resume_emulation / reset_game
- set_breakpoint / remove_breakpoint / list_breakpoints
- get_cpu_state / set_cpu_registers

## Label type taxonomy
Every label must have the most specific correct type. Use these categories consistently:

| Type | When to use | Naming convention |
|------|-------------|-------------------|
| **subroutine** | JSL/JSR target (CDL SubEntryPoint), a callable function | camelCase verb: `initSprites`, `updatePlayer`, `loadPalette` |
| **branch_target** | CDL JumpTarget only (not a subroutine entry), mid-function label | `.loop`, `.done`, `.next` or prefixed: `playerLoop` |
| **pointer_table** | Table of 2-byte (near) or 3-byte (long/far) pointers | `enemyHandlerTable`, `stateJumpTable` |
| **data_table** | Lookup table of values that are not pointers | `sinTable`, `xpThresholdTable`, `tileFlagTable` |
| **graphics** | Tile data, sprite sheets, OBJ attribute data (OAM entries), raw 2bpp/4bpp pixel data | `playerSprite`, `fontTiles`, `enemyGfx` |
| **palette** | CGRAM color data (BGR555 words) | `worldPalette`, `spritePalette0` |
| **tilemap** | Background layer tilemap data | `titlescreenMap`, `level1BgMap` |
| **animation** | Animation frame/sequence data, frame duration tables | `playerWalkAnim`, `explosionFrameTable` |
| **collision** | Hitbox definitions, collision tile flag tables | `enemyHitbox`, `tileCollisionFlags` |
| **text** | Dialogue strings, font index sequences | `introDialogue`, `menuStrings` |
| **music** | SPC700 music sequence data, pattern tables | `bgm_overworld`, `songPatternTable` |
| **sfx** | Sound effect data | `sfx_jump`, `sfxTable` |
| **ai_data** | Enemy behavior scripts, state machine tables | `bossAiScript`, `enemyStateTable` |
| **vector** | Interrupt/reset vectors | `nmiVector`, `resetVector` |
| **variable** | WRAM addresses used as named game variables | `playerHP`, `cameraX`, `frameCounter` |

If a label was assigned the wrong type (e.g. a sprite data block labeled as a subroutine, or a pointer table labeled as raw data), correct it immediately using set_labels.

## Pointer table identification
Pointer tables are one of the most important structures to identify. Recognizing them unlocks many subroutine entry points at once.

Signs of a pointer table:
- Repeated 2-byte or 3-byte values, each mapping to a valid ROM address in the current bank or a specified bank
- Code that does: LDA table,X / STA ptr / JMP (ptr) or JSR (ptr,X) or JSL (ptr) patterns
- An index register (X or Y) scaled by 2 or 3 before being used as the table offset (ASL / multiply by 3)
- The table is referenced by a dispatch routine that switches on a state/type value

To verify: read the bytes with read_memory, interpret as 2- or 3-byte little-endian addresses, check each target with get_disassembly. If all targets are valid code, it is a pointer table — label it as such and label every target as a subroutine.

## Directing the user to uncover code
You are the expert driving coverage. When you see large unreached regions, tell the user specifically what in-game actions will expose them. Be precise:

- ""The $84xxxx bank is unreached. This is likely level data or a secondary engine. Play through the first level and trigger at least one enemy encounter, then come back.""
- ""$80:8200–$80:9FFF is unreached. This overlaps with typical menu/UI code. Open the pause menu, items screen, and map screen.""
- ""Bank $82 looks like music/SFX data. Trigger a few different music tracks and sound effects, then call get_rom_map again.""
- ""There is a large unreached block at $86:0000. Based on the address range in a HiROM game, this may be Mode 7 level data or a cutscene. Try starting a new game and watching the intro.""

After the user does what you asked, call get_rom_map again to assess what new CDL coverage appeared, then annotate the newly reached code.

## SNES / 65816 architecture
- 24-bit address: bank (8b) + offset (16b), written $BBAAAA. LoROM: banks $80–$FF mirror $00–$7F, ROM at offsets $8000–$FFFF. HiROM: banks $C0–$FF hold ROM at $0000–$FFFF.
- Registers: A (accumulator, 8 or 16-bit per M flag), X/Y (index, per X flag), SP, D (direct page base), DB (data bank), PB:PC (program counter)
- REP #$20 → 16-bit A; SEP #$20 → 8-bit A. REP #$10 → 16-bit X/Y; SEP #$10 → 8-bit X/Y
- JSR/RTS = near call/return; JSL/RTL = far (24-bit) call/return; JMP/JML = jumps; Bxx = conditional branches
- CDL flags: Code=1, Data=2, JumpTarget=4, SubEntryPoint=8, X8=16 (X flag was set), M8=32 (M flag was set)
- WRAM: $7E0000–$7FFFFF (banks $00–$3F mirror $0000–$1FFF of WRAM)
- PPU: $2100–$213F; APU I/O: $2140–$2143; DMA: $4300+; SNES I/O: $4200–$42FF
- Hardware vectors: NMI=$00FFEA, IRQ=$00FFEE, RESET=$00FFFC (native mode); emulation at $00FFF[A/C/E]

## Annotation guidelines
- Read 20–40 disassembly lines before naming anything. Understand the full flow first.
- Name from behavior, not bytes: `spawnEnemy` not `sub_808240`
- Comments: what does it do, what does A/X/Y hold on entry, what are side effects or return values
- Common patterns to recognize:
  - Init routine: STZ loops clearing WRAM, LDA/STA loading config, JSL to hardware setup
  - NMI handler: PHA/PHX/PHY push, DMA OAM transfer ($4300+), PLA/PLY/PLX pull, RTI
  - Game loop: JSL dispatch through a state pointer table indexed by a game-state variable
  - DMA transfer: store source addr to $4302–$4304, dest to $2116 or $2118, length to $4305, write $01 to $420B
  - Sprite routine: LDA OAM index, STA $4302, loop writing X/Y/tile/attr words
- When a routine purpose is genuinely ambiguous after reading it, use add_to_review_queue with a specific reason. Do not guess.

## Efficiency rules
- Use get_rom_map instead of separate get_labels + get_annotation_summary calls.
- Use set_labels to apply all annotations from an analysis batch in one call.
- Never re-read an address already in conversation history.
- After annotating a batch, report results then STOP — do not automatically start the next batch without user confirmation.

## When to stop and ask
- If reaching the target code requires specific gameplay (a level, cutscene, boss fight): STOP, tell the user exactly what to do, wait for them.
- If a task could mean multiple systems: ask which one before touching any code.
- After 5+ tool calls with no clear entry point found: STOP, report what you found, state what you need.
- When a task is done: STOP and report. Do not invent follow-on work.

## Communication style
- No preamble. No restating the question. Just act, then report.
- Findings as a table or short list: address | type | name | reason.
- When directing the user to play the game, be specific about what to do and why it will uncover the target code.
- One sentence to explain a block; not a paragraph.

Always read code before labeling it. Correct wrong labels when you find them."
			+ (string.IsNullOrWhiteSpace(_contextText) ? "" : $"\n\n## User-supplied context\n\n{_contextText}");
		}

		private void ClearChat()
		{
			_cts?.Cancel();
			SaveCurrentHistory();
			_tools.Reset();
			_history.Clear();
			Messages.Clear();
			ToolCallLog.Clear();
			AddSystemMessage("Chat cleared.");
		}

		private void AddEntry(ChatEntry.EntryKind kind, string text)
		{
			Messages.Add(new ChatEntry { Kind = kind, Text = text });
		}

		private void AddToolStatus(string text)
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
			_monitor.OnNewItem -= OnMonitorNewItem;
			_monitor.OnBreakpointHit -= OnBreakpointHit;
			_monitor.Dispose();
		}
	}
}
