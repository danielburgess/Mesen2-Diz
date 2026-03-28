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
		[Reactive] public bool ReviewQueueHasItems { get; private set; }

		public ObservableCollection<ChatEntry> Messages { get; } = new();
		public ObservableCollection<ChatEntry> ToolCallLog { get; } = new();
		public ObservableCollection<ReviewQueueItem> ReviewQueue { get; }

		// ── Commands ──────────────────────────────────────────────────────────

		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SendCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelCommand { get; }
		public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearCommand { get; }
		public ReactiveCommand<ReviewQueueItem, System.Reactive.Unit> AnalyzeQueueItemCommand { get; }

		// ── Infrastructure ────────────────────────────────────────────────────

		private readonly ClaudeClient _client = new();
		private readonly AiTools _tools;
		private readonly ExecutionMonitor _monitor;
		private readonly List<JsonObject> _history = new();
		private CancellationTokenSource? _cts;

		public AiCompanionConfig Config => ConfigManager.Config.Debug.AiCompanion;

		// ── Constructor ───────────────────────────────────────────────────────

		public AiCompanionViewModel()
		{
			_monitor = new ExecutionMonitor();
			_tools = new AiTools { Monitor = _monitor };
			ReviewQueue = _monitor.Queue;

			_monitor.OnNewItem += OnMonitorNewItem;
			ReviewQueue.CollectionChanged += (_, _) => ReviewQueueHasItems = ReviewQueue.Count > 0;

			var canSend = this.WhenAnyValue(
				x => x.IsBusy,
				busy => !busy);

			SendCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSend);
			CancelCommand = ReactiveCommand.Create(() => _cts?.Cancel());
			ClearCommand = ReactiveCommand.Create(ClearChat);
			AnalyzeQueueItemCommand = ReactiveCommand.CreateFromTask<ReviewQueueItem>(AnalyzeQueueItemAsync, canSend);

			// Mirror MonitoringMode config to the ExecutionMonitor
			AddDisposable(Config.WhenAnyValue(x => x.MonitoringMode).Subscribe(mode => {
				if(mode != AiMonitoringMode.Disabled) _monitor.Start();
				else _monitor.Stop();
			}));

			AddSystemMessage("AI Companion ready. Type a message or use the Review Queue to analyze unannotated code.");
		}

		// ── Public API ────────────────────────────────────────────────────────

		public void OnGameLoaded()
		{
			_monitor.Reset();
			_tools.Reset();
			_cts?.Cancel();

			// Save previous session before clearing
			SaveCurrentHistory();

			_history.Clear();
			Messages.Clear();
			ToolCallLog.Clear();

			// Load history for newly loaded ROM
			LoadHistory();
		}

		// ── History Persistence ───────────────────────────────────────────────

		private static string GetHistoryPath()
		{
			RomInfo info = EmuApi.GetRomInfo();
			if(string.IsNullOrEmpty(info.GetRomName())) return "";
			return Path.Combine(ConfigManager.DebuggerFolder, info.GetRomName() + ".ai.json");
		}

		private void SaveCurrentHistory()
		{
			try {
				string path = GetHistoryPath();
				if(string.IsNullOrEmpty(path)) return;

				var messagesArray = new JsonArray();
				foreach(var entry in Messages) {
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
			try {
				string path = GetHistoryPath();
				if(string.IsNullOrEmpty(path) || !File.Exists(path)) {
					AddSystemMessage("AI Companion ready. No previous chat history for this ROM.");
					return;
				}

				var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
				if(root == null) { AddSystemMessage("AI Companion ready."); return; }

				if(root["messages"] is JsonArray msgs) {
					foreach(var node in msgs) {
						if(node is not JsonObject obj) continue;
						var kind = (ChatEntry.EntryKind)(obj["kind"]?.GetValue<int>() ?? 0);
						string text = obj["text"]?.GetValue<string>() ?? "";
						DateTime ts = DateTime.TryParse(obj["ts"]?.GetValue<string>(), out var t) ? t : DateTime.Now;
						var entry = new ChatEntry { Kind = kind, Text = text, Timestamp = ts };
						Messages.Add(entry);
						if(kind == ChatEntry.EntryKind.ToolStatus)
							ToolCallLog.Add(entry);
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
			if(string.IsNullOrEmpty(Config.ApiKey)) {
				AddEntry(ChatEntry.EntryKind.Error, "API key not set. Enter your Anthropic API key in the Settings panel (gear icon).");
				return;
			}

			IsBusy = true;
			_cts = new CancellationTokenSource();

			// Add user message to display and history
			AddEntry(ChatEntry.EntryKind.User, userMessage);
			_history.Add(new JsonObject {
				["role"] = "user",
				["content"] = new JsonArray { (JsonNode)new JsonObject { ["type"] = "text", ["text"] = userMessage } }
			});

			// Placeholder for streaming assistant response
			var assistantEntry = new ChatEntry { Kind = ChatEntry.EntryKind.Assistant, IsStreaming = true };
			Messages.Add(assistantEntry);

			try {
				StatusText = "Claude is thinking...";

				await _client.RunTurnAsync(
					apiKey: Config.ApiKey,
					model: Config.Model,
					maxTokens: Config.MaxTokens,
					maxHistoryTurns: Config.MaxHistoryTurns,
					systemPrompt: BuildSystemPrompt(),
					messages: _history,
					tools: _tools.GetDefinitions(),
					toolExecutor: (name, input) => _tools.ExecuteAsync(name, input),
					onTextDelta: delta => {
						Dispatcher.UIThread.Post(() => assistantEntry.AppendText(delta));
					},
					onToolStatus: status => {
						if(Config.ShowToolCalls) {
							Dispatcher.UIThread.Post(() =>
								AddToolStatus(status));
						}
					},
					ct: _cts.Token);

				assistantEntry.IsStreaming = false;
				StatusText = "Ready";
			} catch(OperationCanceledException) {
				assistantEntry.IsStreaming = false;
				assistantEntry.AppendText("\n[Cancelled]");
				StatusText = "Cancelled";
			} catch(Exception ex) {
				assistantEntry.IsStreaming = false;
				if(string.IsNullOrEmpty(assistantEntry.Text))
					Messages.Remove(assistantEntry);
				AddEntry(ChatEntry.EntryKind.Error, $"Error: {ex.Message}");
				StatusText = "Error";
			} finally {
				IsBusy = false;
				_cts?.Dispose();
				_cts = null;
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

		private string BuildSystemPrompt()
		{
			int romSize = DebugApi.GetMemorySize(MemoryType.SnesPrgRom);
			string romInfo = romSize > 0
				? $"A {romSize / 1024}KB SNES ROM is currently loaded."
				: "No ROM is currently loaded.";

			return $@"You are an expert SNES (Super Nintendo Entertainment System) reverse-engineering assistant integrated into the Mesen2-Diz emulator/debugger. Your primary role is to help annotate SNES ROM disassembly by identifying and labeling functions, branches, data structures, and code patterns.

{romInfo}

You have access to tools that let you:
- Read disassembly at any address (get_disassembly)
- Read emulated RAM, ROM, and memory regions (read_memory)
- View Code/Data Logger (CDL) coverage flags per ROM byte (get_cdl_data)
- Add, modify, or delete labels and comments (set_label, delete_label)
- View all existing labels (get_labels, get_label_at)
- Read the current call stack (get_call_stack)
- Get annotation coverage statistics (get_annotation_summary)
- Queue addresses for deferred review (add_to_review_queue)

SNES / 65816 architecture notes:
- 24-bit address space: bank (8 bits) + offset (16 bits), written as $BBAAAA
- Registers: A (accumulator), X/Y (index), S (stack pointer), D (direct page), DB (data bank), PB/PC
- M flag: 0=16-bit accumulator, 1=8-bit; X flag: 0=16-bit index, 1=8-bit
- REP #$20 clears M (16-bit A); SEP #$20 sets M (8-bit A). Similarly for X with #$10
- JSR/JSL for subroutine calls (RTS/RTL to return), JMP/JML for jumps, BRA/BEQ/BNE/etc for branches
- CDL SubEntryPoint flag marks JSR/JSL targets (subroutine starts); JumpTarget marks branch destinations
- WRAM at $7E0000–$7FFFFF (mirrored banks $00–$3F offset $0000–$1FFF)
- PPU registers $2100–$213F; APU I/O $2140–$2143; DMA $4300+; SNES registers $4200–$42FF

When annotating code:
- Use descriptive camelCase or snake_case label names (e.g. initSprites, player_update)
- Comments should explain: what the routine does, key parameters (in A/X/Y/D), return values, side effects
- Look for common patterns: init routines (clear RAM, load palettes), NMI/VBlank handlers, game-loop calls, DMA transfers, sprite/tilemap updates
- When encountering a routine for the first time, read 20–40 lines of disassembly to understand it before labeling
- If context is ambiguous, queue the address with add_to_review_queue rather than guessing

Efficiency rules (important — each tool call has an API cost):
- Never call get_disassembly or read_memory for an address you have already read in this conversation — the data is already visible in the conversation history above.
- When annotating multiple functions in one session, batch your set_label calls after analysis rather than interleaving reads and writes for each one.
- Prefer get_annotation_summary once at the start of a session rather than calling it repeatedly.

Be systematic, thorough, and use your tools freely. Always read the code before labeling it.";
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
			Messages.Add(entry);
			ToolCallLog.Add(entry);
		}

		private void AddSystemMessage(string text)
			=> AddEntry(ChatEntry.EntryKind.System, text);

		protected override void DisposeView()
		{
			_cts?.Cancel();
			SaveCurrentHistory();
			_monitor.OnNewItem -= OnMonitorNewItem;
			_monitor.Dispose();
			_client.Dispose();
		}
	}
}
