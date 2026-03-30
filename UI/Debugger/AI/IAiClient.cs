using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Debugger.AI
{
	public interface IAiClient : IDisposable
	{
		Task RunTurnAsync(
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
			CancellationToken ct);
	}
}
