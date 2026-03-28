using System.Collections.ObjectModel;
using Mesen.Debugger.AI;
using ReactiveUI;

namespace Mesen.Debugger.ViewModels
{
	/// <summary>
	/// Thin wrapper exposing the queue/tool-log data from <see cref="AiCompanionViewModel"/>
	/// so the dock factory can give it a distinct type for DataTemplate routing.
	/// </summary>
	public class AiCompanionQueueViewModel
	{
		public AiCompanionViewModel Parent { get; }

		public ObservableCollection<ReviewQueueItem> ReviewQueue => Parent.ReviewQueue;
		public ObservableCollection<ChatEntry> ToolCallLog => Parent.ToolCallLog;
		public ReactiveCommand<ReviewQueueItem, System.Reactive.Unit> AnalyzeQueueItemCommand => Parent.AnalyzeQueueItemCommand;
		public bool ReviewQueueHasItems => Parent.ReviewQueueHasItems;

		public AiCompanionQueueViewModel(AiCompanionViewModel parent)
		{
			Parent = parent;
		}
	}
}
