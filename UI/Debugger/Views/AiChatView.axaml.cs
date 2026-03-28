using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mesen.Debugger.ViewModels;
using System;

namespace Mesen.Debugger.Views
{
	public class AiChatView : UserControl
	{
		private AiCompanionViewModel? _model;

		public AiChatView()
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
			if(DataContext is AiCompanionViewModel model) {
				_model = model;
				model.Messages.CollectionChanged += (_, _) => {
					Dispatcher.UIThread.Post(() => {
						this.FindControl<ScrollViewer>("chatScroll")?.ScrollToEnd();
					}, DispatcherPriority.Background);
				};
			}
		}

		private void OnInputKeyDown(object? sender, KeyEventArgs e)
		{
			if(e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _model != null) {
				e.Handled = true;
				if(!_model.IsBusy)
					_model.SendCommand.Execute().Subscribe();
			}
		}

		private void OnSendCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if(_model == null) return;
			if(_model.IsBusy)
				_model.CancelCommand.Execute().Subscribe();
			else
				_model.SendCommand.Execute().Subscribe();
		}
	}
}
