using System.Collections.Generic;
using ReactiveUI.Fody.Helpers;

namespace Mesen.Config
{
	public enum AiMonitoringMode
	{
		Disabled,
		Queue,     // collect unannotated branch/sub targets for later review
		AutoPause  // pause execution and immediately query Claude
	}

	public class AiCompanionConfig : BaseWindowConfig<AiCompanionConfig>
	{
		[Reactive] public string ApiKey { get; set; } = "";
		[Reactive] public string Model { get; set; } = "claude-haiku-4-5-20251001";
		public List<string> Models { get; set; } = new() {
			"claude-haiku-4-5-20251001",
			"claude-sonnet-4-6",
			"claude-opus-4-6"
		};
		[Reactive] public int MaxTokens { get; set; } = 4096;
		[Reactive] public int MaxHistoryTurns { get; set; } = 10;
		[Reactive] public int MaxToolCallsPerTurn { get; set; } = 20;
		[Reactive] public AiMonitoringMode MonitoringMode { get; set; } = AiMonitoringMode.Queue;
		[Reactive] public bool ShowToolCalls { get; set; } = true;
		[Reactive] public string ContextFilePath { get; set; } = "";
	}
}
