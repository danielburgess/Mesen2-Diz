using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mesen.Debugger.ViewModels;
using System;

namespace Mesen.Debugger.Views
{
	public class AiQueueView : UserControl
	{
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
				model.ToolCallLog.CollectionChanged += (_, _) => {
					Dispatcher.UIThread.Post(() => {
						this.FindControl<ScrollViewer>("toolLogScroll")?.ScrollToEnd();
					}, DispatcherPriority.Background);
				};
			}
		}
	}
}
