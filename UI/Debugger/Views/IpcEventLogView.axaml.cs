using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Mesen.Debugger.ViewModels;

namespace Mesen.Debugger.Views
{
	public class IpcEventLogView : UserControl
	{
		private ListBox? _eventList;
		private bool _scrollLocked = true;

		public IpcEventLogView()
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
			if(DataContext is IpcEventLogViewModel vm) {
				vm.Events.CollectionChanged += OnEventsChanged;
			}
		}

		protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
		{
			base.OnAttachedToVisualTree(e);
			_eventList = this.FindControl<ListBox>("EventList");
		}

		private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if(_scrollLocked && _eventList != null && DataContext is IpcEventLogViewModel vm && vm.Events.Count > 0) {
				_eventList.ScrollIntoView(vm.Events[vm.Events.Count - 1]);
			}
		}

		private void OnScrollLockClick(object? sender, RoutedEventArgs e)
		{
			if(sender is ToggleButton toggle) {
				_scrollLocked = toggle.IsChecked == true;
			}
		}
	}
}
