using System.Collections.Generic;
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

		/// <summary>
		/// Returns recent history trimmed to at most <paramref name="maxTurns"/> real user turns.
		/// Never splits a tool_use/tool_result pair across the trim boundary.
		/// </summary>
		internal static List<JsonObject> TrimHistory(List<JsonObject> messages, int maxTurns)
		{
			if(maxTurns <= 0) return messages;

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

			if(turnStarts.Count <= maxTurns) return messages;

			int startIdx = turnStarts[turnStarts.Count - maxTurns];
			return messages.GetRange(startIdx, messages.Count - startIdx);
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
