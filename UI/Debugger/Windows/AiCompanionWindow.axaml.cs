using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mesen.Config;
using Mesen.Debugger.AI;
using Mesen.Debugger.ViewModels;
using Mesen.Interop;
using Mesen.Utilities;
using Mesen.Windows;
using System;

namespace Mesen.Debugger.Windows
{
	public class AiCompanionWindow : MesenWindow, INotificationHandler
	{
		private readonly AiCompanionViewModel _model;

		[Obsolete("For designer only")]
		public AiCompanionWindow() : this(new AiCompanionViewModel()) { }

		public AiCompanionWindow(AiCompanionViewModel model)
		{
			InitializeComponent();
			_model = model;
			DataContext = model;
			model.Config.LoadWindowSettings(this);
#if DEBUG
			this.AttachDevTools();
#endif
		}

		private void InitializeComponent()
		{
			AvaloniaXamlLoader.Load(this);
		}

		protected override void OnOpened(EventArgs e)
		{
			base.OnOpened(e);
			this.GetControl<TextBox>("txtInput").Focus();
		}

		protected override void OnClosing(WindowClosingEventArgs e)
		{
			base.OnClosing(e);
			_model.Config.SaveWindowSettings(this);
			ConfigManager.Config.Debug.AiCompanion = _model.Config;
		}

		private void OnInputKeyDown(object? sender, KeyEventArgs e)
		{
			if(e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) {
				e.Handled = true;
				if(!_model.IsBusy)
					_model.SendCommand.Execute().Subscribe();
			}
		}

		public void OnSendCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if(_model.IsBusy)
				_model.CancelCommand.Execute().Subscribe();
			else
				_model.SendCommand.Execute().Subscribe();
		}

		private async void OnLoadContextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		string? path = await FileDialogHelper.OpenFile(null, this, "txt", "md", "*");
		if(path != null)
			_model.LoadContextFile(path);
	}

	public void OnAnalyzeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if(sender is Button btn && btn.Tag is Mesen.Debugger.AI.ReviewQueueItem item)
				_model.AnalyzeQueueItemCommand.Execute(item).Subscribe();
		}

		public void ProcessNotification(NotificationEventArgs e)
		{
			if(e.NotificationType == ConsoleNotificationType.GameLoaded) {
				Dispatcher.UIThread.Post(() => _model.OnGameLoaded());
			}
		}

		// Scroll to bottom whenever a new message arrives
		protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
		{
			base.OnAttachedToVisualTree(e);
			_model.Messages.CollectionChanged += (_, _) => {
				Dispatcher.UIThread.Post(() => {
					var scroll = this.FindControl<ScrollViewer>("chatScroll");
					scroll?.ScrollToEnd();
				}, DispatcherPriority.Background);
			};
		}
	}
}
