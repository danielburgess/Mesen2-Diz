using System.Collections.ObjectModel;

namespace Mesen.Debugger.ViewModels
{
	/// <summary>
	/// Thin wrapper exposing the tool-log data from <see cref="AiCompanionViewModel"/>
	/// so the dock factory can give it a distinct type for DataTemplate routing.
	/// </summary>
	public class AiCompanionQueueViewModel
	{
		public AiCompanionViewModel Parent { get; }

		public ObservableCollection<ChatEntry> ToolCallLog => Parent.ToolCallLog;

		public AiCompanionQueueViewModel(AiCompanionViewModel parent)
		{
			Parent = parent;
		}
	}
}
