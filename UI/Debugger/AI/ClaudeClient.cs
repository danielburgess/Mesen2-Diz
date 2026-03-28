using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Debugger.AI
{
	/// <summary>
	/// Lightweight Claude API client with streaming and agentic tool-call loop.
	/// </summary>
	public class ClaudeClient : IDisposable
	{
		private static readonly HttpClient _http = new HttpClient();
		private const string ApiUrl = "https://api.anthropic.com/v1/messages";
		private const string ApiVersion = "2023-06-01";

		public void Dispose() { }

		/// <summary>
		/// Runs one full agentic turn: streams Claude's response, executes any tool calls,
		/// then continues until Claude produces a final end_turn response.
		/// </summary>
		/// <param name="apiKey">Anthropic API key.</param>
		/// <param name="model">Model ID.</param>
		/// <param name="maxTokens">Max output tokens.</param>
		/// <param name="systemPrompt">System prompt.</param>
		/// <param name="messages">Conversation history — modified in place with assistant + tool result messages.</param>
		/// <param name="tools">Tool definitions as JsonObjects with name/description/input_schema.</param>
		/// <param name="toolExecutor">Called for each tool use; returns the string result.</param>
		/// <param name="onTextDelta">Called for each streaming text fragment.</param>
		/// <param name="onToolStatus">Called when a tool is about to execute (UI feedback).</param>
		/// <param name="ct">Cancellation token.</param>
		public async Task RunTurnAsync(
			string apiKey,
			string model,
			int maxTokens,
			int maxHistoryTurns,
			int maxToolCallsPerTurn,
			string systemPrompt,
			List<JsonObject> messages,
			List<JsonObject> tools,
			Func<string, JsonObject, Task<string>> toolExecutor,
			Action<string> onTextDelta,
			Action<string> onToolStatus,
			CancellationToken ct)
		{
			int toolCallCount = 0;
			while(true) {
				ct.ThrowIfCancellationRequested();

				// Remove any trailing assistant message whose tool_use blocks have no matching
				// tool_result in the next message. This can be left behind if a previous turn
				// was cancelled or threw after the assistant message was appended but before
				// the tool results were added.
				StripOrphanedToolUse(messages);

				var (textParts, toolUses, stopReason) = await StreamOneRequest(
					apiKey, model, maxTokens, systemPrompt, TrimHistory(messages, maxHistoryTurns), tools, onTextDelta, ct);

				// Build the assistant message from what we accumulated
				var assistantContent = new JsonArray();
				if(textParts.Count > 0) {
					assistantContent.Add((JsonNode)new JsonObject {
						["type"] = "text",
						["text"] = string.Join("", textParts)
					});
				}
				foreach(var tu in toolUses) {
					assistantContent.Add((JsonNode)tu.ContentBlock.DeepClone());
				}
				messages.Add(new JsonObject {
					["role"] = "assistant",
					["content"] = assistantContent
				});

				if(stopReason != "tool_use" || toolUses.Count == 0)
					break;

				// Execute tools and collect results
				var toolResultContent = new JsonArray();
				foreach(var tu in toolUses) {
					onToolStatus($"[tool: {tu.Name}]");
					string result;
					try {
						result = await toolExecutor(tu.Name, tu.Input);
					} catch(Exception ex) {
						result = $"Error executing tool: {ex.Message}";
					}
					toolResultContent.Add((JsonNode)new JsonObject {
						["type"] = "tool_result",
						["tool_use_id"] = tu.Id,
						["content"] = result
					});
				}
				messages.Add(new JsonObject {
					["role"] = "user",
					["content"] = toolResultContent
				});

				toolCallCount += toolUses.Count;
				if(maxToolCallsPerTurn > 0 && toolCallCount >= maxToolCallsPerTurn) {
					onToolStatus($"[tool call limit ({maxToolCallsPerTurn}) reached]");
					// Inject a system notice, then do one final no-tools request so Claude summarises
					messages.Add(new JsonObject {
						["role"] = "user",
						["content"] = new JsonArray {
							(JsonNode)new JsonObject {
								["type"] = "text",
								["text"] = $"[System: the tool call limit of {maxToolCallsPerTurn} for this turn has been reached. Stop using tools now. Summarize what you have done and what still needs to be done. If you need the user to take an action first (such as running the game to a specific point so the code is reachable), say so explicitly and wait for their reply.]"
							}
						}
					});
					var (summaryParts, _, _) = await StreamOneRequest(
						apiKey, model, maxTokens, systemPrompt, TrimHistory(messages, maxHistoryTurns), new List<JsonObject>(), onTextDelta, ct);
					// Record the summary so the next turn doesn't see an orphaned user message
					if(summaryParts.Count > 0) {
						messages.Add(new JsonObject {
							["role"] = "assistant",
							["content"] = new JsonArray {
								(JsonNode)new JsonObject {
									["type"] = "text",
									["text"] = string.Join("", summaryParts)
								}
							}
						});
					}
					break;
				}
				// Loop: send again with tool results
			}
		}

		private async Task<(List<string> textParts, List<ToolUseAccum> toolUses, string stopReason)>
			StreamOneRequest(
				string apiKey,
				string model,
				int maxTokens,
				string systemPrompt,
				List<JsonObject> messages,
				List<JsonObject> tools,
				Action<string> onTextDelta,
				CancellationToken ct)
		{
			// System prompt sent as a cached block — Anthropic charges only 10% for cache hits
			var systemBlock = new JsonArray {
				(JsonNode)new JsonObject {
					["type"] = "text",
					["text"] = systemPrompt,
					["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
				}
			};
			var body = new JsonObject {
				["model"] = model,
				["max_tokens"] = maxTokens,
				["system"] = systemBlock,
				["stream"] = true,
				["messages"] = CloneArray(messages),
				["tools"] = CloneArray(tools)
			};

			string bodyJson = body.ToJsonString();

			// Retry with backoff on 429 rate-limit errors
			HttpResponseMessage response;
			int[] retryDelays = { 5, 10, 20, 40 };
			int attempt = 0;
			while(true) {
				using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
				request.Headers.Add("x-api-key", apiKey);
				request.Headers.Add("anthropic-version", ApiVersion);
				request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");
				request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
				response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
				if((int)response.StatusCode != 429 || attempt >= retryDelays.Length) break;
				response.Dispose();
				int delaySecs = retryDelays[attempt++];
				onTextDelta($"\n[Rate limited — retrying in {delaySecs}s...]");
				await Task.Delay(delaySecs * 1000, ct);
			}

			using(response) {

			if(!response.IsSuccessStatusCode) {
				var errorBody = await response.Content.ReadAsStringAsync(ct);
				throw new InvalidOperationException($"Claude API error {(int)response.StatusCode}: {errorBody}");
			}

			var textParts = new List<string>();
			var toolUses = new Dictionary<int, ToolUseAccum>();
			string stopReason = "end_turn";

			using var stream = await response.Content.ReadAsStreamAsync(ct);
			using var reader = new StreamReader(stream);

			string? line;
			while((line = await reader.ReadLineAsync()) != null) {
				ct.ThrowIfCancellationRequested();
				if(!line.StartsWith("data: ")) continue;
				var data = line.Substring(6).Trim();
				if(data == "[DONE]") break;

				JsonDocument doc;
				try { doc = JsonDocument.Parse(data); }
				catch { continue; }

				using(doc) {
					var root = doc.RootElement;
					if(!root.TryGetProperty("type", out var typeProp)) continue;
					string evtType = typeProp.GetString() ?? "";

					switch(evtType) {
						case "content_block_start":
							if(root.TryGetProperty("content_block", out var cb) &&
								root.TryGetProperty("index", out var idxProp)) {
								int idx = idxProp.GetInt32();
								string cbType = cb.GetProperty("type").GetString() ?? "";
								if(cbType == "tool_use") {
									toolUses[idx] = new ToolUseAccum {
										Id = cb.GetProperty("id").GetString() ?? "",
										Name = cb.GetProperty("name").GetString() ?? "",
										// Preserve the whole block for assistant history
										ContentBlock = JsonNode.Parse(cb.GetRawText()) as JsonObject
											?? new JsonObject()
									};
								}
							}
							break;

						case "content_block_delta":
							if(root.TryGetProperty("delta", out var delta) &&
								root.TryGetProperty("index", out var dIdx)) {
								int idx = dIdx.GetInt32();
								string deltaType = delta.GetProperty("type").GetString() ?? "";
								if(deltaType == "text_delta") {
									string text = delta.GetProperty("text").GetString() ?? "";
									textParts.Add(text);
									onTextDelta(text);
								} else if(deltaType == "input_json_delta" && toolUses.TryGetValue(idx, out var tu)) {
									tu.InputJson.Append(delta.GetProperty("partial_json").GetString() ?? "");
								}
							}
							break;

						case "message_delta":
							if(root.TryGetProperty("delta", out var msgDelta) &&
								msgDelta.TryGetProperty("stop_reason", out var sr)) {
								stopReason = sr.GetString() ?? "end_turn";
							}
							break;
					}
				}
			}

			// Parse accumulated tool inputs
			var toolUseList = new List<ToolUseAccum>();
			foreach(var kv in toolUses) {
				var tu = kv.Value;
				var inputStr = tu.InputJson.ToString();
				if(string.IsNullOrWhiteSpace(inputStr)) inputStr = "{}";
				try {
					tu.Input = JsonNode.Parse(inputStr) as JsonObject ?? new JsonObject();
					// Patch the content block's input with the fully accumulated value
					tu.ContentBlock["input"] = JsonNode.Parse(inputStr);
				} catch {
					tu.Input = new JsonObject();
				}
				toolUseList.Add(tu);
			}

			return (textParts, toolUseList, stopReason);

			} // end using(response)
		}

		/// <summary>
		/// Removes trailing assistant messages that contain tool_use blocks without a matching
		/// user message of tool_results immediately following. Such entries are left behind when
		/// a turn is cancelled or throws after the assistant message is appended but before the
		/// tool results are added, and they cause a 400 on the next API call.
		/// Mutates <paramref name="messages"/> in place.
		/// </summary>
		private static void StripOrphanedToolUse(List<JsonObject> messages)
		{
			if(messages.Count == 0) return;

			int last = messages.Count - 1;
			var msg = messages[last];
			if(msg["role"]?.GetValue<string>() != "assistant") return;

			var content = msg["content"] as JsonArray;
			if(content == null) return;

			// Collect all tool_use ids in this assistant message
			var toolUseIds = new HashSet<string>();
			foreach(var block in content) {
				if(block is JsonObject b &&
				   b["type"]?.GetValue<string>() == "tool_use" &&
				   b["id"]?.GetValue<string>() is string id)
					toolUseIds.Add(id);
			}

			if(toolUseIds.Count == 0) return; // no tool_use — history is valid

			// The last message is an assistant with tool_use but has no following tool_result —
			// it is orphaned. Remove it so the next API call starts from a valid state.
			messages.RemoveAt(last);
		}

				/// <summary>
		/// Returns recent history trimmed to at most <paramref name="maxTurns"/> real user turns.
		/// Trims only at turn-start boundaries (regular user messages, not tool_result messages)
		/// so that tool_use/tool_result pairs are never split across the trim point.
		/// </summary>
		private static List<JsonObject> TrimHistory(List<JsonObject> messages, int maxTurns)
		{
			if(maxTurns <= 0)
				return messages;

			// Collect indices of "real" user turn starts — user messages that are NOT tool_results
			var turnStarts = new List<int>();
			for(int i = 0; i < messages.Count; i++) {
				var msg = messages[i];
				if(msg["role"]?.GetValue<string>() == "user") {
					bool isToolResult = msg["content"] is JsonArray arr && arr.Count > 0 &&
						(arr[0] as JsonObject)?["type"]?.GetValue<string>() == "tool_result";
					if(!isToolResult)
						turnStarts.Add(i);
				}
			}

			if(turnStarts.Count <= maxTurns)
				return messages;

			int startIdx = turnStarts[turnStarts.Count - maxTurns];
			return messages.GetRange(startIdx, messages.Count - startIdx);
		}

		private static JsonArray CloneArray(List<JsonObject> items)
		{
			var arr = new JsonArray();
			foreach(var item in items)
				arr.Add(item.DeepClone());
			return arr;
		}
	}

	internal class ToolUseAccum
	{
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public StringBuilder InputJson { get; } = new();
		public JsonObject Input { get; set; } = new();
		public JsonObject ContentBlock { get; set; } = new();
	}
}
