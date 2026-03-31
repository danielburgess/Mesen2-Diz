using System.Collections.Generic;
using ReactiveUI.Fody.Helpers;

namespace Mesen.Config
{
	public static class AiModelLists
	{
		public static readonly List<string> ClaudeModels = new() {
			"claude-opus-4-6",
			"claude-sonnet-4-6",
			"claude-haiku-4-5-20251001",
		};

		public static readonly List<string> LocalModels = new() {
			"llama3",
			"mistral",
			"gemma3",
			"qwen2.5-coder",
			"deepseek-r1",
		};
	}

	public enum AiProvider
	{
		Claude,
		OpenAiCompatible  // Ollama, LM Studio, vLLM, or any OpenAI-compatible server
	}

	public class AiCompanionConfig : BaseWindowConfig<AiCompanionConfig>
	{
		[Reactive] public AiProvider Provider { get; set; } = AiProvider.Claude;

		// Claude (Anthropic)
		[Reactive] public string ApiKey { get; set; } = "";

		// OpenAI-compatible local / self-hosted
		[Reactive] public string LocalApiEndpoint { get; set; } = "http://localhost:11434/v1";
		[Reactive] public string LocalApiKey { get; set; } = "";

		[Reactive] public string Model { get; set; } = "claude-sonnet-4-6";
		[Reactive] public int MaxTokens { get; set; } = 4096;
		[Reactive] public int MaxHistoryTurns { get; set; } = 10;
		[Reactive] public int MaxToolCallsPerTurn { get; set; } = 20;
		[Reactive] public string ContextFilePath { get; set; } = "";
	}
}
