using Mesen.Annotation;
using Mesen.Annotation.Diz;
using Mesen.Config;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using Mesen.Utilities;
using Mesen.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mesen.Debugger.Diz
{
	/// <summary>
	/// Loads a DiztinGUIsh .diz or .dizraw project file into the running Mesen
	/// debugger: CDL flags are pushed via <c>DebugApi.SetCdlData</c> and labels
	/// are pushed via <c>LabelManager.SetLabels</c>.
	/// </summary>
	public static class DizWorkspaceLoader
	{
		public static void LoadFile(string path, bool showResult)
		{
			string xml;
			try {
				xml = ReadDizFile(path);
			} catch(Exception ex) {
				MesenMsgBox.Show(null, "DizLoadError", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.Message);
				return;
			}

			RomAnnotationStore store;
			try {
				store = DizProjectImporter.Import(xml);
			} catch(Exception ex) {
				MesenMsgBox.Show(null, "DizLoadError", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.Message);
				return;
			}

			// ── CDL ───────────────────────────────────────────────────────────
			MemoryType memType = MemoryType.SnesPrgRom;
			int liveSize = DebugApi.GetMemorySize(memType);
			if(liveSize > 0) {
				byte[] cdl   = DizToMesenAdapter.ToCdlDataBytes(store);
				int    count = Math.Min(cdl.Length, liveSize);
				DebugApi.SetCdlData(memType, cdl, count);
			}

			// ── Labels ────────────────────────────────────────────────────────
			string mlb = DizToMesenAdapter.ToMlbText(store);
			var labels  = new List<CodeLabel>();
			int errors  = 0;
			foreach(string row in mlb.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
				CodeLabel? label = CodeLabel.FromString(row);
				if(label == null) {
					errors++;
				} else {
					if(ConfigManager.Config.Debug.Integration.IsMemoryTypeImportEnabled(label.MemoryType)) {
						if(label.Label.Length > 0 || ConfigManager.Config.Debug.Integration.ImportComments) {
							labels.Add(label);
						}
					}
				}
			}

			LabelManager.SetLabels(labels);

			if(showResult) {
				MesenMsgBox.Show(null,
					errors == 0 ? "ImportLabels" : "ImportLabelsWithErrors",
					MessageBoxButtons.OK, MessageBoxIcon.Info,
					labels.Count.ToString(), errors.ToString());
			}
		}

		private static string ReadDizFile(string path)
		{
			if(Path.GetExtension(path).Equals("." + FileDialogHelper.DizExt, StringComparison.OrdinalIgnoreCase)) {
				// .diz is gzip-compressed XML
				using var fs     = File.OpenRead(path);
				using var gz     = new GZipStream(fs, CompressionMode.Decompress);
				using var reader = new StreamReader(gz, Encoding.UTF8);
				return reader.ReadToEnd();
			}
			// .dizraw is plain UTF-8 XML
			return File.ReadAllText(path, Encoding.UTF8);
		}
	}
}
