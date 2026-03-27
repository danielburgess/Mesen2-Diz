using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DataBoxControl;
using Mesen.Config;
using Mesen.Debugger.ViewModels;
using Mesen.Debugger.Labels;
using Mesen.Debugger.Windows;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using static Mesen.Debugger.ViewModels.LabelListViewModel;

namespace Mesen.Debugger.Views
{
	public class LabelListView : UserControl
	{
		public LabelListView()
		{
			InitializeComponent();
		}

		private void InitializeComponent()
		{
			AvaloniaXamlLoader.Load(this);
		}

		protected override void OnDataContextChanged(EventArgs e)
		{
			if(DataContext is LabelListViewModel model) {
				model.InitContextMenu(this);
				BindEditableColumnReadOnly();
			}
			base.OnDataContextChanged(e);
		}

		/// <summary>
		/// Binds the <see cref="DataBoxEditableTextColumn.IsReadOnly"/> property on each
		/// editable column to the inverse of <see cref="DebuggerConfig.InlineLabelEditEnabled"/>
		/// so that toggling the setting immediately affects all visible cells.
		/// </summary>
		private void BindEditableColumnReadOnly()
		{
			var dataBox = this.GetControl<DataBox>("labelDataBox");
			var isReadOnlyObs = ConfigManager.Config.Debug.Debugger
				.WhenAnyValue(x => x.InlineLabelEditEnabled)
				.Select(enabled => !enabled);

			foreach(var col in dataBox.Columns) {
				if(col is DataBoxEditableTextColumn editCol)
					editCol.Bind(DataBoxEditableTextColumn.IsReadOnlyProperty, isReadOnlyObs);
			}
		}

		private void OnCellDoubleClick(DataBoxCell cell)
		{
			if(DataContext is LabelListViewModel listModel && cell.DataContext is LabelViewModel label) {
				// When inline editing is active, double-click on an editable column selects
				// the text inside the TextBox naturally — don't open the full edit window.
				if(ConfigManager.Config.Debug.Debugger.InlineLabelEditEnabled
					&& cell.Column is DataBoxEditableTextColumn)
					return;

				LabelEditWindow.EditLabel(listModel.CpuType, this, label.Label);
			}
		}
	}
}
