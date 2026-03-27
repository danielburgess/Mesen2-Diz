using Mesen.Debugger.Labels;
using Mesen.Debugger.Utilities;
using Mesen.Utilities;
using Mesen.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;

namespace Mesen.Debugger.ViewModels
{
	public class LabelFindReplaceViewModel : DisposableViewModel
	{
		[Reactive] public string FindText    { get; set; } = "";
		[Reactive] public string ReplaceText { get; set; } = "";
		[Reactive] public bool   MatchCase   { get; set; } = false;
		[Reactive] public int    MatchCount  { get; private set; } = 0;

		public LabelFindReplaceViewModel()
		{
			// Recompute match count whenever find text or match-case changes.
			AddDisposable(
				this.WhenAnyValue(x => x.FindText, x => x.MatchCase)
					.Throttle(TimeSpan.FromMilliseconds(100))
					.ObserveOn(ReactiveUI.RxApp.MainThreadScheduler)
					.Subscribe(_ => MatchCount = CountMatches())
			);
		}

		private int CountMatches()
		{
			if(FindText.Length == 0) return 0;

			StringComparison cmp = MatchCase
				? StringComparison.Ordinal
				: StringComparison.OrdinalIgnoreCase;

			return LabelManager.GetAllLabels()
				.Count(l => l.Comment.Contains(FindText, cmp));
		}

		public int ReplaceAll()
		{
			if(FindText.Length == 0) return 0;

			StringComparison cmp = MatchCase
				? StringComparison.Ordinal
				: StringComparison.OrdinalIgnoreCase;

			List<CodeLabel> targets = LabelManager.GetAllLabels()
				.Where(l => l.Comment.Contains(FindText, cmp))
				.ToList();

			if(targets.Count == 0) return 0;

			LabelManager.SuspendEvents();
			try {
				foreach(CodeLabel label in targets) {
					CodeLabel updated = label.Clone();
					updated.Comment = MatchCase
						? updated.Comment.Replace(FindText, ReplaceText, StringComparison.Ordinal)
						: ReplaceInvariant(updated.Comment, FindText, ReplaceText);

					LabelManager.DeleteLabel(label, false);
					LabelManager.SetLabel(updated, false);
				}
			} finally {
				LabelManager.ResumeEvents();
			}

			DebugWorkspaceManager.AutoSave();
			MatchCount = CountMatches();
			return targets.Count;
		}

		/// <summary>Case-insensitive replace that preserves the case of surrounding text.</summary>
		private static string ReplaceInvariant(string input, string find, string replacement)
		{
			int idx = input.IndexOf(find, StringComparison.OrdinalIgnoreCase);
			if(idx < 0) return input;

			var sb = new System.Text.StringBuilder(input.Length);
			int last = 0;
			while(idx >= 0) {
				sb.Append(input, last, idx - last);
				sb.Append(replacement);
				last = idx + find.Length;
				idx = input.IndexOf(find, last, StringComparison.OrdinalIgnoreCase);
			}
			sb.Append(input, last, input.Length - last);
			return sb.ToString();
		}
	}
}
