namespace DataBoxControl;

/// <summary>
/// Implemented by row view-model objects that support in-place cell editing
/// via <see cref="DataBoxEditableTextColumn"/>.
/// </summary>
public interface IInlineEditable
{
	/// <summary>Persist any pending edits to the underlying data store.</summary>
	void CommitInlineEdit();

	/// <summary>Discard any pending edits and revert cells to the stored values.</summary>
	void CancelInlineEdit();
}
