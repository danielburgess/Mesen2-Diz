using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Mesen.Debugger.Views
{
	public class IpcEventLogView : UserControl
	{
		public IpcEventLogView()
		{
			InitializeComponent();
		}

		private void InitializeComponent()
		{
			AvaloniaXamlLoader.Load(this);
		}
	}
}
