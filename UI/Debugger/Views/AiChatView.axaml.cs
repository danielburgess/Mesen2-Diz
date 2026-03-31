using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Mesen.Debugger.ViewModels;
using Mesen.Utilities;
using System;

namespace Mesen.Debugger.Views
{
	public class AiChatView : UserControl
	{
		private AiCompanionViewModel? _model;
		private bool _suppressScrollLockUpdate = false;

		public AiChatView()
		{
			InitializeComponent();

			// Register with Tunnel routing so we intercept Enter BEFORE the TextBox's
			// internal AcceptsReturn handler inserts a newline character.
			var txtInput = this.FindControl<TextBox>("txtInput")!;
			txtInput.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
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
				// No longer auto-scroll here — OnChatScrollChanged handles it
			}
		}

		private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
		{
			if(_suppressScrollLockUpdate) return;
			var scroll = (ScrollViewer)sender!;

			if(e.ExtentDelta.Y > 0) {
				// Content grew — auto-scroll if locked
				if(_model?.ChatScrollLocked == true) {
					_suppressScrollLockUpdate = true;
					scroll.ScrollToEnd();
					_suppressScrollLockUpdate = false;
				}
			} else if(e.OffsetDelta.Y != 0 && _model != null) {
				// User manually scrolled — update lock based on whether at bottom
				bool atBottom = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 8;
				_model.ChatScrollLocked = atBottom;
			}
		}

		private void OnInputKeyDown(object? sender, KeyEventArgs e)
		{
			if(e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _model != null) {
				e.Handled = true;
				if(!_model.IsBusy)
					_model.SendCommand.Execute().Subscribe();
			}
			// Shift+Enter falls through to TextBox default (inserts newline, AcceptsReturn=True)
		}

		private void OnSendCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if(_model == null) return;
			if(_model.IsBusy)
				_model.CancelCommand.Execute().Subscribe();
			else
				_model.SendCommand.Execute().Subscribe();
		}

		private async void OnLoadContextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if(_model == null) return;
			string? path = await FileDialogHelper.OpenFile(null, VisualRoot, "txt", "md", "*");
			if(path != null)
				_model.LoadContextFile(path);
		}
	}
}
