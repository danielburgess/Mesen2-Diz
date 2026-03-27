using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Mesen.Debugger.ViewModels;
using Mesen.Utilities;
using Mesen.Windows;
using System;

namespace Mesen.Debugger.Windows
{
	public class LabelFindReplaceWindow : MesenWindow
	{
		private readonly LabelFindReplaceViewModel _model;

		[Obsolete("For designer only")]
		public LabelFindReplaceWindow() : this(new()) { }

		public LabelFindReplaceWindow(LabelFindReplaceViewModel model)
		{
			InitializeComponent();
			_model = model;
			DataContext = model;
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
			this.GetControl<TextBox>("txtFind").FocusAndSelectAll();
		}

		protected override void OnClosing(WindowClosingEventArgs e)
		{
			base.OnClosing(e);
			_model.Dispose();
		}

		/// <summary>Opens (or focuses) the non-modal Find/Replace window.</summary>
		public static void Open(Control parent)
		{
			// Reuse an existing instance if already open.
			if(parent.GetVisualRoot() is Window owner) {
				foreach(Window w in owner.OwnedWindows) {
					if(w is LabelFindReplaceWindow existing) {
						existing.Activate();
						existing.GetControl<TextBox>("txtFind").FocusAndSelectAll();
						return;
					}
				}
				var wnd = new LabelFindReplaceWindow(new LabelFindReplaceViewModel());
				wnd.Show(owner);
			}
		}

		private void OnReplaceAllClick(object sender, RoutedEventArgs e)
		{
			_model.ReplaceAll();
		}

		private void OnCloseClick(object sender, RoutedEventArgs e)
		{
			Close();
		}
	}
}
