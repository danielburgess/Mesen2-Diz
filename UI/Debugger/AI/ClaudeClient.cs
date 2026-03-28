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
					await StreamOneRequest(
						apiKey, model, maxTokens, systemPrompt, TrimHistory(messages, maxHistoryTurns), new List<JsonObject>(), onTextDelta, ct);
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

			using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
			request.Headers.Add("x-api-key", apiKey);
			request.Headers.Add("anthropic-version", ApiVersion);
			request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");
			request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

			using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

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
		}

		/// <summary>Returns the last <paramref name="maxTurns"/> user/assistant pairs from history.</summary>
		private static List<JsonObject> TrimHistory(List<JsonObject> messages, int maxTurns)
		{
			if(maxTurns <= 0 || messages.Count <= maxTurns * 2)
				return messages;
			// Keep the most recent maxTurns*2 messages (each turn = user + assistant)
			return messages.GetRange(messages.Count - maxTurns * 2, maxTurns * 2);
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
