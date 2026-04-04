using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using DataBoxControl.Primitives;
using System.Linq;
using System.Reactive.Linq;

namespace DataBoxControl;

/// <summary>
/// A DataBox column that renders each cell as a <see cref="TextBox"/> so the
/// user can edit the value directly in the table without opening a dialog.
/// When <see cref="IsReadOnly"/> is <c>true</c> the TextBox is non-interactive
/// (hit-test disabled) and behaves like the regular <see cref="DataBoxTextColumn"/>.
/// </summary>
public class DataBoxEditableTextColumn : DataBoxBoundColumn
{
	public static readonly StyledProperty<bool> IsReadOnlyProperty =
		AvaloniaProperty.Register<DataBoxEditableTextColumn, bool>(nameof(IsReadOnly), defaultValue: false);

	public static readonly StyledProperty<bool> ShowToolTipProperty =
		AvaloniaProperty.Register<DataBoxEditableTextColumn, bool>(nameof(ShowToolTip), defaultValue: false);

	public bool IsReadOnly
	{
		get => GetValue(IsReadOnlyProperty);
		set => SetValue(IsReadOnlyProperty, value);
	}

	public bool ShowToolTip
	{
		get => GetValue(ShowToolTipProperty);
		set => SetValue(ShowToolTipProperty, value);
	}

	public DataBoxEditableTextColumn()
	{
		CellTemplate = new FuncDataTemplate(
			_ => true,
			(_, _) => {
				var textBox = new TextBox {
					BorderThickness = new Thickness(0),
					Background = Brushes.Transparent,
					VerticalAlignment = VerticalAlignment.Stretch,
					AcceptsReturn = false,
					[!Layoutable.MarginProperty] = new DynamicResourceExtension("DataGridTextColumnCellTextBlockMargin"),
				};

				if(Binding is { })
					textBox.Bind(TextBox.TextProperty, Binding);

				if(ShowToolTip && Binding is { })
					textBox.Bind(ToolTip.TipProperty, Binding);

				// React to column-level IsReadOnly changes so all live cells update immediately.
				var isReadOnlyObs = this.GetObservable(IsReadOnlyProperty);
				textBox.Bind(TextBox.IsReadOnlyProperty, isReadOnlyObs);
				// Disable hit-testing when read-only so clicks pass through to the row for selection.
				textBox.Bind(TextBox.IsHitTestVisibleProperty, isReadOnlyObs.Select(ro => !ro));

				textBox.KeyDown += OnKeyDown;
				textBox.LostFocus += OnLostFocus;
				textBox.GotFocus += OnGotFocus;

				return textBox;
			},
			supportsRecycling: false);
	}

	private static void OnGotFocus(object? sender, GotFocusEventArgs e)
	{
		if(sender is not TextBox tb) return;
		// When the TextBox captures a pointer click the PointerPressed event is consumed
		// before it bubbles to the ListBox row, so the row never gets selected.
		// Manually select the parent row here to ensure the highlight follows the edit cursor.
		var row = tb.FindAncestorOfType<DataBoxRow>();
		var presenter = tb.FindAncestorOfType<DataBoxRowsPresenter>();
		if(row != null && presenter != null) {
			int index = presenter.GetRowIndex(row);
			if(index >= 0 && presenter.SelectedIndex != index)
				presenter.SelectedIndex = index;
		}
	}

	private static void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if(sender is not TextBox tb) return;

		if(e.Key == Key.Return) {
			(tb.DataContext as IInlineEditable)?.CommitInlineEdit();
			// Shift focus to the top-level window, clearing focus from the TextBox.
			TopLevel.GetTopLevel(tb)?.Focus();
			e.Handled = true;
		} else if(e.Key == Key.Escape) {
			(tb.DataContext as IInlineEditable)?.CancelInlineEdit();
			TopLevel.GetTopLevel(tb)?.Focus();
			e.Handled = true;
		}
	}

	private static void OnLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		if(sender is TextBox { IsReadOnly: false } tb)
			(tb.DataContext as IInlineEditable)?.CommitInlineEdit();
	}
}
