using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Debugger.AI
{
	/// <summary>
	/// AI client for any OpenAI-compatible endpoint (Ollama, LM Studio, vLLM, etc.).
	/// Accepts and stores conversation history in Claude format for compatibility with the
	/// shared history persistence layer; converts to/from OpenAI wire format internally.
	/// </summary>
	public class OpenAiCompatibleClient : IAiClient
	{
		private static readonly HttpClient _http = new HttpClient();
		private readonly string _baseUrl;

		public OpenAiCompatibleClient(string baseUrl)
		{
			_baseUrl = baseUrl.TrimEnd('/');
		}

		public void Dispose() { }

		public async Task RunTurnAsync(
			string apiKey,
			string model,
			int maxTokens,
			int maxHistoryTurns,
			int maxToolCallsPerTurn,
			string systemPrompt,
			List<JsonObject> messages,
			List<JsonObject> claudeTools,
			Func<string, JsonObject, Task<string>> toolExecutor,
			Action<string> onTextDelta,
			Action<string> onToolStatus,
			CancellationToken ct)
		{
			var openAiTools = ConvertTools(claudeTools);

			int toolCallCount = 0;
			while(true) {
				ct.ThrowIfCancellationRequested();
				AiMessageHelpers.StripOrphanedToolUse(messages);

				var trimmed = AiMessageHelpers.TrimHistory(messages, maxHistoryTurns);
				var openAiMessages = ConvertToOpenAiMessages(systemPrompt, trimmed);

				var (textParts, toolCalls, _) = await StreamOneRequest(
					apiKey, model, maxTokens, openAiMessages,
					openAiTools.Count > 0 ? openAiTools : null,
					onTextDelta, ct);

				// Record assistant turn in Claude format so history stays portable
				var assistantContent = new JsonArray();
				if(textParts.Count > 0)
					assistantContent.Add((JsonNode)new JsonObject {
						["type"] = "text",
						["text"] = string.Join("", textParts)
					});
				foreach(var tc in toolCalls)
					assistantContent.Add((JsonNode)new JsonObject {
						["type"] = "tool_use",
						["id"] = tc.Id,
						["name"] = tc.Name,
						["input"] = tc.Input.DeepClone()
					});
				messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContent });

				if(toolCalls.Count == 0) break;

				// Execute tools, record results in Claude format
				var toolResultContent = new JsonArray();
				foreach(var tc in toolCalls) {
					onToolStatus($"[tool: {tc.Name}]");
					string result;
					try {
						result = await toolExecutor(tc.Name, tc.Input);
					} catch(Exception ex) {
						result = $"Error executing tool: {ex.Message}";
					}
					toolResultContent.Add((JsonNode)new JsonObject {
						["type"] = "tool_result",
						["tool_use_id"] = tc.Id,
						["content"] = result
					});
				}
				messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResultContent });

				toolCallCount += toolCalls.Count;
				if(maxToolCallsPerTurn > 0 && toolCallCount >= maxToolCallsPerTurn) {
					onToolStatus($"[tool call limit ({maxToolCallsPerTurn}) reached]");
					messages.Add(new JsonObject {
						["role"] = "user",
						["content"] = new JsonArray {
							(JsonNode)new JsonObject {
								["type"] = "text",
								["text"] = $"[System: the tool call limit of {maxToolCallsPerTurn} for this turn has been reached. Stop using tools now. Summarize what you have done and what still needs to be done.]"
							}
						}
					});
					var trimmed2 = AiMessageHelpers.TrimHistory(messages, maxHistoryTurns);
					var msgs2 = ConvertToOpenAiMessages(systemPrompt, trimmed2);
					var (summaryParts, _, _) = await StreamOneRequest(apiKey, model, maxTokens, msgs2, null, onTextDelta, ct);
					if(summaryParts.Count > 0)
						messages.Add(new JsonObject {
							["role"] = "assistant",
							["content"] = new JsonArray {
								(JsonNode)new JsonObject { ["type"] = "text", ["text"] = string.Join("", summaryParts) }
							}
						});
					break;
				}
			}
		}

		// ── Conversion: Claude format → OpenAI messages ──────────────────────────

		private static List<JsonObject> ConvertToOpenAiMessages(string systemPrompt, List<JsonObject> claudeMessages)
		{
			var result = new List<JsonObject>();
			result.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });

			foreach(var msg in claudeMessages) {
				string role = msg["role"]?.GetValue<string>() ?? "user";
				var content = msg["content"] as JsonArray;
				if(content == null) continue;

				if(role == "user") {
					bool isToolResults = content.Count > 0 &&
						(content[0] as JsonObject)?["type"]?.GetValue<string>() == "tool_result";

					if(isToolResults) {
						// Each tool result → separate "tool" role message
						foreach(var block in content) {
							if(block is not JsonObject b) continue;
							result.Add(new JsonObject {
								["role"] = "tool",
								["tool_call_id"] = b["tool_use_id"]?.GetValue<string>() ?? "",
								["content"] = b["content"]?.GetValue<string>() ?? ""
							});
						}
					} else {
						// Regular user text — concatenate all text blocks
						var sb = new StringBuilder();
						foreach(var block in content)
							if(block is JsonObject b && b["type"]?.GetValue<string>() == "text")
								sb.Append(b["text"]?.GetValue<string>() ?? "");
						result.Add(new JsonObject { ["role"] = "user", ["content"] = sb.ToString() });
					}
				} else if(role == "assistant") {
					var textSb = new StringBuilder();
					var toolCallsArr = new JsonArray();

					foreach(var block in content) {
						if(block is not JsonObject b) continue;
						string type = b["type"]?.GetValue<string>() ?? "";
						if(type == "text") {
							textSb.Append(b["text"]?.GetValue<string>() ?? "");
						} else if(type == "tool_use") {
							string inputJson = b["input"]?.ToJsonString() ?? "{}";
							toolCallsArr.Add((JsonNode)new JsonObject {
								["id"] = b["id"]?.GetValue<string>() ?? "",
								["type"] = "function",
								["function"] = new JsonObject {
									["name"] = b["name"]?.GetValue<string>() ?? "",
									["arguments"] = inputJson
								}
							});
						}
					}

					var assistantMsg = new JsonObject {
						["role"] = "assistant",
						["content"] = textSb.ToString()
					};
					if(toolCallsArr.Count > 0)
						assistantMsg["tool_calls"] = toolCallsArr;
					result.Add(assistantMsg);
				}
			}

			return result;
		}

		/// <summary>
		/// Converts Claude-format tool definitions (input_schema) to OpenAI format (parameters).
		/// </summary>
		private static List<JsonObject> ConvertTools(List<JsonObject> claudeTools)
		{
			var result = new List<JsonObject>();
			foreach(var t in claudeTools) {
				result.Add(new JsonObject {
					["type"] = "function",
					["function"] = new JsonObject {
						["name"] = t["name"]?.DeepClone(),
						["description"] = t["description"]?.DeepClone(),
						["parameters"] = t["input_schema"]?.DeepClone()
					}
				});
			}
			return result;
		}

		// ── HTTP / streaming ──────────────────────────────────────────────────────

		private async Task<(List<string> textParts, List<OpenAiToolCallAccum> toolCalls, string finishReason)>
			StreamOneRequest(
				string apiKey,
				string model,
				int maxTokens,
				List<JsonObject> messages,
				List<JsonObject>? tools,
				Action<string> onTextDelta,
				CancellationToken ct)
		{
			var body = new JsonObject {
				["model"] = model,
				["max_tokens"] = maxTokens,
				["stream"] = true,
				["messages"] = AiMessageHelpers.CloneArray(messages)
			};

			if(tools != null && tools.Count > 0) {
				var toolsArr = new JsonArray();
				foreach(var t in tools) toolsArr.Add(t.DeepClone());
				body["tools"] = toolsArr;
			}

			string bodyJson = body.ToJsonString();
			string url = _baseUrl + "/chat/completions";

			HttpResponseMessage response;
			int[] retryDelays = { 5, 10, 20, 40 };
			int attempt = 0;
			while(true) {
				using var request = new HttpRequestMessage(HttpMethod.Post, url);
				if(!string.IsNullOrEmpty(apiKey))
					request.Headers.Add("Authorization", $"Bearer {apiKey}");
				request.Content = new System.Net.Http.StringContent(bodyJson, Encoding.UTF8, "application/json");
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
					throw new InvalidOperationException($"API error {(int)response.StatusCode}: {errorBody}");
				}

				var textParts = new List<string>();
				var toolCalls = new Dictionary<int, OpenAiToolCallAccum>();
				string finishReason = "stop";

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
						if(!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
						var choice = choices[0];

						if(choice.TryGetProperty("finish_reason", out var fr) &&
						   fr.ValueKind != JsonValueKind.Null)
							finishReason = fr.GetString() ?? "stop";

						if(!choice.TryGetProperty("delta", out var delta)) continue;

						// Text content
						if(delta.TryGetProperty("content", out var contentProp) &&
						   contentProp.ValueKind == JsonValueKind.String) {
							string text = contentProp.GetString() ?? "";
							if(text.Length > 0) {
								textParts.Add(text);
								onTextDelta(text);
							}
						}

						// Tool calls (streamed incrementally by index)
						if(delta.TryGetProperty("tool_calls", out var tcs)) {
							foreach(var tc in tcs.EnumerateArray()) {
								int idx = tc.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : 0;
								if(!toolCalls.TryGetValue(idx, out var accum)) {
									accum = new OpenAiToolCallAccum();
									toolCalls[idx] = accum;
								}
								if(tc.TryGetProperty("id", out var idProp) &&
								   idProp.ValueKind == JsonValueKind.String)
									accum.Id = idProp.GetString() ?? accum.Id;
								if(tc.TryGetProperty("function", out var func)) {
									if(func.TryGetProperty("name", out var nameProp) &&
									   nameProp.ValueKind == JsonValueKind.String)
										accum.Name = nameProp.GetString() ?? accum.Name;
									if(func.TryGetProperty("arguments", out var argsProp) &&
									   argsProp.ValueKind == JsonValueKind.String)
										accum.ArgumentsJson.Append(argsProp.GetString() ?? "");
								}
							}
						}
					}
				}

				// Parse accumulated tool inputs
				var toolCallList = new List<OpenAiToolCallAccum>();
				foreach(var kv in toolCalls) {
					var tc = kv.Value;
					string argsStr = tc.ArgumentsJson.ToString();
					if(string.IsNullOrWhiteSpace(argsStr)) argsStr = "{}";
					try { tc.Input = JsonNode.Parse(argsStr) as JsonObject ?? new JsonObject(); }
					catch { tc.Input = new JsonObject(); }
					toolCallList.Add(tc);
				}

				return (textParts, toolCallList, finishReason);
			}
		}
	}

	internal class OpenAiToolCallAccum
	{
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public StringBuilder ArgumentsJson { get; } = new();
		public JsonObject Input { get; set; } = new();
	}
}
