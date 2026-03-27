using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mesen.Config;
using Mesen.Debugger.Controls;
using Mesen.Debugger.ViewModels;
using Mesen.Interop;
using Mesen.Windows;
using System;
using System.ComponentModel;

namespace Mesen.Debugger.Windows
{
	public class RomMapWindow : MesenWindow, INotificationHandler
	{
		private RomMapViewModel _model;

		[Obsolete("For designer only")]
		public RomMapWindow() : this(true) { }

		public RomMapWindow(bool unused = false)
		{
			InitializeComponent();
#if DEBUG
			this.AttachDevTools();
#endif
			ScrollPictureViewer scrollViewer = this.GetControl<ScrollPictureViewer>("picViewer");
			PictureViewer picViewer = scrollViewer.InnerViewer;

			_model = new RomMapViewModel(picViewer, scrollViewer, this);
			DataContext = _model;
			_model.Config.LoadWindowSettings(this);
		}

		private void InitializeComponent()
		{
			AvaloniaXamlLoader.Load(this);
		}

		protected override void OnOpened(EventArgs e)
		{
			base.OnOpened(e);
			if(Design.IsDesignMode) return;
			_model.RefreshData();
		}

		protected override void OnClosing(WindowClosingEventArgs e)
		{
			base.OnClosing(e);
			_model.Config.SaveWindowSettings(this);
			ConfigManager.Config.Debug.RomMapViewer = _model.Config;
		}

		private void OnSettingsClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			_model.Config.ShowSettingsPanel = !_model.Config.ShowSettingsPanel;
		}

		private void OnFitToWidthClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			_model.FitToWidth();
		}

		public void ProcessNotification(NotificationEventArgs e)
		{
			// ROM map data is static once loaded; re-render only when a new game/project loads.
			if(e.NotificationType == ConsoleNotificationType.GameLoaded) {
				Dispatcher.UIThread.Post(() => _model.RefreshData());
			}
		}
	}
}
