using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace Mesen.Debugger.AI
{
	/// <summary>
	/// Shared message-manipulation helpers used by both Claude and OpenAI-compatible clients.
	/// History is always stored in Claude format regardless of which client is active.
	/// </summary>
	internal static class AiMessageHelpers
	{
		/// <summary>
		/// Removes trailing assistant messages that contain tool_use blocks with no matching
		/// tool_result in the immediately following user message. Such orphans are left behind
		/// when a turn is cancelled after the assistant message is appended but before the tool
		/// results are added; they cause a 400 on the next API call.
		/// </summary>
		internal static void StripOrphanedToolUse(List<JsonObject> messages)
		{
			for(int i = 0; i < messages.Count; i++) {
				var msg = messages[i];
				if(msg["role"]?.GetValue<string>() != "assistant") continue;

				var content = msg["content"] as JsonArray;
				if(content == null) continue;

				var toolUseIds = new System.Collections.Generic.HashSet<string>();
				foreach(var block in content) {
					if(block is JsonObject b &&
					   b["type"]?.GetValue<string>() == "tool_use" &&
					   b["id"]?.GetValue<string>() is string id)
						toolUseIds.Add(id);
				}

				if(toolUseIds.Count == 0) continue;

				bool matched = false;
				if(i + 1 < messages.Count) {
					var next = messages[i + 1];
					if(next["role"]?.GetValue<string>() == "user" &&
					   next["content"] is JsonArray nextContent) {
						matched = true;
						foreach(string tuId in toolUseIds) {
							bool found = false;
							foreach(var block in nextContent) {
								if(block is JsonObject b &&
								   b["type"]?.GetValue<string>() == "tool_result" &&
								   b["tool_use_id"]?.GetValue<string>() == tuId) {
									found = true;
									break;
								}
							}
							if(!found) { matched = false; break; }
						}
					}
				}

				if(matched) continue;
				messages.RemoveAt(i);
				i--;
			}
		}

		// Budget for message history tokens. Claude Sonnet/Opus limit is 131k total.
		// System prompt (~8k) + tool definitions (~2k) + max completion (6k) ≈ 16k overhead.
		// 60k leaves ~55k headroom — conservative enough to handle estimation error.
		private const int TokenBudget = 60_000;

		// Tool results larger than this are truncated in the API payload.
		// _history keeps the full data; only what gets sent to the API is capped.
		// 12k chars ≈ 3k tokens — enough for the AI to act on a result without
		// a single tool call monopolising the entire context window.
		private const int MaxToolResultChars = 12_000;

		/// <summary>
		/// Returns recent history trimmed to at most <paramref name="maxTurns"/> real user turns,
		/// then further trimmed if the estimated token count still exceeds <see cref="TokenBudget"/>.
		/// Never splits a tool_use/tool_result pair across the trim boundary.
		/// Always preserves at least the most recent turn regardless of size.
		/// Tool results that exceed <see cref="MaxToolResultChars"/> are truncated in the returned
		/// slice; the originals in <paramref name="messages"/> are not modified.
		/// </summary>
		internal static List<JsonObject> TrimHistory(List<JsonObject> messages, int maxTurns)
		{
			if(maxTurns <= 0) return TruncateToolResults(messages);

			// Build the list of indexes where real user turns start (ignoring tool_result messages)
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

			if(turnStarts.Count == 0) return TruncateToolResults(messages);

			// Start at the maxTurns boundary, then walk forward until under the token budget.
			// The loop always stops at least one turn before the end, guaranteeing the most
			// recent turn is always included.
			int keepFrom = turnStarts.Count <= maxTurns ? 0 : turnStarts.Count - maxTurns;

			while(keepFrom < turnStarts.Count - 1) {
				var candidate = messages.GetRange(turnStarts[keepFrom], messages.Count - turnStarts[keepFrom]);
				if(EstimateTokens(candidate) <= TokenBudget)
					return TruncateToolResults(candidate);
				keepFrom++;
			}

			// Last resort: return only the most recent turn (still truncated for safety)
			return TruncateToolResults(
				messages.GetRange(turnStarts[turnStarts.Count - 1], messages.Count - turnStarts[turnStarts.Count - 1]));
		}

		/// <summary>
		/// Returns a copy of <paramref name="messages"/> where any tool_result whose content
		/// exceeds <see cref="MaxToolResultChars"/> is replaced with a truncated clone.
		/// Messages that do not need truncation are returned by reference (no allocation).
		/// </summary>
		private static List<JsonObject> TruncateToolResults(List<JsonObject> messages)
		{
			List<JsonObject>? result = null;  // allocated lazily only if any truncation occurs

			for(int i = 0; i < messages.Count; i++) {
				var msg = messages[i];

				// Only user messages can contain tool_result blocks
				if(msg["role"]?.GetValue<string>() != "user" ||
				   msg["content"] is not JsonArray content) {
					result?.Add(msg);
					continue;
				}

				// Scan for oversized tool_result blocks
				bool needsTruncation = false;
				foreach(var block in content) {
					if(block is JsonObject b &&
					   b["type"]?.GetValue<string>() == "tool_result" &&
					   ToolResultString(b) is string s &&
					   s.Length > MaxToolResultChars) {
						needsTruncation = true;
						break;
					}
				}

				if(!needsTruncation) {
					result?.Add(msg);
					continue;
				}

				// Switch to explicit list on first truncation, back-filling previous entries
				if(result == null) {
					result = new List<JsonObject>(messages.Count);
					for(int j = 0; j < i; j++) result.Add(messages[j]);
				}

				// Deep-clone this message and truncate oversized tool results within it
				var cloned = (JsonObject)msg.DeepClone();
				if(cloned["content"] is JsonArray clonedContent) {
					foreach(var block in clonedContent) {
						if(block is not JsonObject b) continue;
						if(b["type"]?.GetValue<string>() != "tool_result") continue;
						string? s = ToolResultString(b);
						if(s != null && s.Length > MaxToolResultChars) {
							int omitted = s.Length - MaxToolResultChars;
							b["content"] =
								$"[TRUNCATED: This tool result was cut from {s.Length:N0} to {MaxToolResultChars:N0} chars " +
								$"({omitted:N0} chars / ~{omitted / 3:N0} tokens omitted) to fit the context window. " +
								$"The data below is incomplete — do not assume the missing portion is absent from the ROM; " +
								$"call the tool again with a narrower scope if you need the rest.]\n\n" +
								s[..MaxToolResultChars];
						}
					}
				}
				result.Add(cloned);
			}

			return result ?? messages;
		}

		/// <summary>Extracts the string content from a tool_result block, or null if not a plain string.</summary>
		private static string? ToolResultString(JsonObject b)
		{
			var node = b["content"];
			if(node is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
			return null;
		}

		/// <summary>
		/// Token estimate: serialize to JSON and divide by 3.
		/// Mixed English+code content is roughly 3 chars/token (more conservative than the
		/// commonly cited 4, which is for plain prose).
		/// </summary>
		private static int EstimateTokens(List<JsonObject> messages)
		{
			int chars = 0;
			foreach(var msg in messages)
				chars += msg.ToJsonString().Length;
			return chars / 3;
		}

		/// <summary>
		/// Converts a list of conversation messages into a compact human-readable text block
		/// suitable for use as a summarization prompt. Large tool results are truncated.
		/// </summary>
		internal static string BuildCompactionPrompt(List<JsonObject> messages)
		{
			var sb = new StringBuilder();
			sb.AppendLine(
				"Below is an excerpt of a prior conversation between a user and an AI assistant " +
				"helping reverse-engineer a SNES ROM in the Mesen2-Diz debugger. " +
				"Summarize what happened: which tools were called and what they found, " +
				"which labels and comments were applied (preserve exact names and addresses), " +
				"which ROM areas were analyzed, any code patterns or data structures identified, " +
				"and any unresolved tasks or open questions.");
			sb.AppendLine("Be concise but preserve all specific actionable facts.");
			sb.AppendLine();
			sb.AppendLine("--- CONVERSATION EXCERPT ---");
			sb.AppendLine();

			foreach(var msg in messages) {
				string role = msg["role"]?.GetValue<string>() ?? "?";
				if(msg["content"] is not JsonArray content) continue;

				foreach(var block in content) {
					if(block is not JsonObject b) continue;
					string type = b["type"]?.GetValue<string>() ?? "";

					switch(type) {
						case "text": {
							string text = b["text"]?.GetValue<string>() ?? "";
							if(!string.IsNullOrWhiteSpace(text))
								sb.AppendLine($"[{role}]: {text.Trim()}");
							break;
						}
						case "tool_use": {
							string name = b["name"]?.GetValue<string>() ?? "?";
							string input = b["input"]?.ToJsonString() ?? "{}";
							if(input.Length > 300) input = input[..300] + "...";
							sb.AppendLine($"[tool: {name}({input})]");
							break;
						}
						case "tool_result": {
							string result = "";
							var contentNode = b["content"];
							if(contentNode is JsonValue jv && jv.TryGetValue<string>(out var s))
								result = s;
							else if(contentNode is JsonArray rc && rc.Count > 0 && rc[0] is JsonObject rb)
								result = rb["text"]?.GetValue<string>() ?? "";
							if(result.Length > 400) result = result[..400] + "...(truncated)";
							if(!string.IsNullOrWhiteSpace(result))
								sb.AppendLine($"[tool result]: {result.Trim()}");
							break;
						}
					}
				}
			}

			return sb.ToString();
		}

		internal static JsonArray CloneArray(List<JsonObject> items)
		{
			var arr = new JsonArray();
			foreach(var item in items)
				arr.Add(item.DeepClone());
			return arr;
		}
	}
}
