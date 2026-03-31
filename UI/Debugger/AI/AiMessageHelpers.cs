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

		// Conservative budget leaving room for system prompt + completion.
		// Claude Sonnet/Opus is 131k; local models vary. 90k keeps us safely clear.
		private const int TokenBudget = 90_000;

		/// <summary>
		/// Returns recent history trimmed to at most <paramref name="maxTurns"/> real user turns,
		/// then further trimmed if the estimated token count still exceeds <see cref="TokenBudget"/>.
		/// Never splits a tool_use/tool_result pair across the trim boundary.
		/// Always preserves at least the most recent turn regardless of size.
		/// </summary>
		internal static List<JsonObject> TrimHistory(List<JsonObject> messages, int maxTurns)
		{
			if(maxTurns <= 0) return messages;

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

			if(turnStarts.Count == 0) return messages;

			// Start at the maxTurns boundary, then walk forward until under the token budget.
			// The loop always stops at least one turn before the end, guaranteeing the most
			// recent turn is always included.
			int keepFrom = turnStarts.Count <= maxTurns ? 0 : turnStarts.Count - maxTurns;

			while(keepFrom < turnStarts.Count - 1) {
				var candidate = messages.GetRange(turnStarts[keepFrom], messages.Count - turnStarts[keepFrom]);
				if(EstimateTokens(candidate) <= TokenBudget)
					return candidate;
				keepFrom++;
			}

			// Last resort: return only the most recent turn
			return messages.GetRange(turnStarts[turnStarts.Count - 1], messages.Count - turnStarts[turnStarts.Count - 1]);
		}

		/// <summary>
		/// Rough token estimate: serialize to JSON and divide by 4 (≈4 chars/token for mixed
		/// English + code content). Intentionally over-estimates to stay conservative.
		/// </summary>
		private static int EstimateTokens(List<JsonObject> messages)
		{
			int chars = 0;
			foreach(var msg in messages)
				chars += msg.ToJsonString().Length;
			return chars / 4;
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
