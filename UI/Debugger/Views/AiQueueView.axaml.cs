using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mesen.Debugger.AI;
using Mesen.Debugger.ViewModels;
using System;

namespace Mesen.Debugger.Views
{
	public class AiQueueView : UserControl
	{
		private AiCompanionQueueViewModel? _model;

		public AiQueueView()
		{
			InitializeComponent();
		}

		private void InitializeComponent()
		{
			AvaloniaXamlLoader.Load(this);
		}

		protected override void OnDataContextChanged(EventArgs e)
		{
			base.OnDataContextChanged(e);
			if(DataContext is AiCompanionQueueViewModel model) {
				_model = model;
				model.ToolCallLog.CollectionChanged += (_, _) => {
					Dispatcher.UIThread.Post(() => {
						this.FindControl<ScrollViewer>("toolLogScroll")?.ScrollToEnd();
					}, DispatcherPriority.Background);
				};
			}
		}

		private void OnAnalyzeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if(sender is Button btn && btn.Tag is ReviewQueueItem item && _model != null)
				_model.AnalyzeQueueItemCommand.Execute(item).Subscribe();
		}
	}
}
