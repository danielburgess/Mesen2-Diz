using Mesen.Annotation;
using Mesen.Annotation.Asm;
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
	/// Loads and exports DiztinGUIsh .diz/.dizraw project files to/from the
	/// running Mesen debugger.
	///
	/// After a successful import the store is retained in <see cref="CurrentStore"/>
	/// so that <see cref="ExportFile"/> can merge live CDL data back into it.
	///
	/// <see cref="ExportFile"/> also works without a prior import: when
	/// <see cref="CurrentStore"/> is <c>null</c> it builds a fresh store from
	/// the live CDL data, ROM header, and active labels.
	/// </summary>
	public static class DizWorkspaceLoader
	{
		/// <summary>The most recently imported store, or <c>null</c> if none.</summary>
		public static RomAnnotationStore? CurrentStore { get; private set; }

		/// <summary>
		/// Returns <see cref="CurrentStore"/> if one exists, otherwise builds a
		/// temporary store from the currently running ROM's CDL data.
		/// Returns <c>null</c> if no ROM is loaded.
		/// </summary>
		public static RomAnnotationStore? GetOrBuildStore()
		{
			if(CurrentStore != null) return CurrentStore;
			int liveSize = DebugApi.GetMemorySize(MemoryType.SnesPrgRom);
			return liveSize > 0 ? BuildStoreFromMesen(MemoryType.SnesPrgRom, liveSize) : null;
		}

		// ── Import ────────────────────────────────────────────────────────────

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
			string mlb    = DizToMesenAdapter.ToMlbText(store);
			var    labels = new List<CodeLabel>();
			int    errors = 0;
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

			// Keep store alive for export.
			CurrentStore = store;

			if(showResult) {
				MesenMsgBox.Show(null,
					errors == 0 ? "ImportLabels" : "ImportLabelsWithErrors",
					MessageBoxButtons.OK, MessageBoxIcon.Info,
					labels.Count.ToString(), errors.ToString());
			}
		}

		// ── Export ────────────────────────────────────────────────────────────

		/// <summary>
		/// Merge live Mesen CDL data into the current store (or build one from
		/// scratch if none was imported), then write the result to
		/// <paramref name="path"/> as .dizraw or gzip-compressed .diz.
		/// </summary>
		public static void ExportFile(string path)
		{
			MemoryType memType  = MemoryType.SnesPrgRom;
			int        liveSize = DebugApi.GetMemorySize(memType);

			RomAnnotationStore? base_ = CurrentStore;

			if(base_ == null) {
				// No project was imported — build a fresh store from Mesen state.
				base_ = BuildStoreFromMesen(memType, liveSize);
				if(base_ == null) {
					MesenMsgBox.Show(null, "DizExportNoRom", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
			}

			// Merge live CDL into the store.
			RomAnnotationStore updated = base_;
			if(liveSize > 0) {
				CdlFlags[] cdlFlags = DebugApi.GetCdlData(0, (uint)liveSize, memType);
				byte[]     cdlBytes = new byte[cdlFlags.Length];
				for(int i = 0; i < cdlFlags.Length; i++)
					cdlBytes[i] = (byte)cdlFlags[i];
				updated = DizToMesenAdapter.MergeFromCdlData(base_, cdlBytes);
			}

			CurrentStore = updated;

			try {
				string xml = DizProjectExporter.Export(updated);
				WriteDizFile(path, xml);
			} catch(Exception ex) {
				MesenMsgBox.Show(null, "DizExportError", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.Message);
				return;
			}

			MesenMsgBox.Show(null, "DizExportSuccess", MessageBoxButtons.OK, MessageBoxIcon.Info,
				Path.GetFileName(path));
		}

		// ── ASM export ────────────────────────────────────────────────────────

		/// <summary>
		/// Merge live CDL data into the current store (or build one from scratch),
		/// disassemble the ROM, and write Asar-compatible .asm text to
		/// <paramref name="path"/>.
		/// </summary>
		public static void ExportAsmFile(string path)
		{
			MemoryType memType  = MemoryType.SnesPrgRom;
			int        liveSize = DebugApi.GetMemorySize(memType);

			RomAnnotationStore? base_ = CurrentStore;
			if(base_ == null) {
				base_ = BuildStoreFromMesen(memType, liveSize);
				if(base_ == null) {
					MesenMsgBox.Show(null, "DizExportNoRom", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
			}

			RomAnnotationStore updated = base_;
			if(liveSize > 0) {
				CdlFlags[] cdlFlags = DebugApi.GetCdlData(0, (uint)liveSize, memType);
				byte[]     cdlBytes = new byte[cdlFlags.Length];
				for(int i = 0; i < cdlFlags.Length; i++)
					cdlBytes[i] = (byte)cdlFlags[i];
				updated = DizToMesenAdapter.MergeFromCdlData(base_, cdlBytes);
			}

			CurrentStore = updated;

			byte[] romBytes = DebugApi.GetMemoryState(memType);

			try {
				string asm = SnesAsmExporter.Export(updated, romBytes);
				File.WriteAllText(path, asm, new System.Text.UTF8Encoding(false));
			} catch(Exception ex) {
				MesenMsgBox.Show(null, "DizExportError", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.Message);
				return;
			}

			MesenMsgBox.Show(null, "DizExportSuccess", MessageBoxButtons.OK, MessageBoxIcon.Info,
				Path.GetFileName(path));
		}

		// ── Build store from live Mesen state ─────────────────────────────────

		private static RomAnnotationStore? BuildStoreFromMesen(MemoryType memType, int liveSize)
		{
			if(liveSize <= 0)
				return null;

			// Read the full PRG ROM to parse the SNES internal header.
			byte[] romBytes = DebugApi.GetMemoryState(memType);

			TryDetectSnesHeader(romBytes, out RomMapMode mapMode, out RomSpeed speed, out string gameName, out uint checksum);

			// Build ByteAnnotation[] from live CDL flags.
			CdlFlags[] cdlFlags = DebugApi.GetCdlData(0, (uint)liveSize, memType);
			var bytes = new ByteAnnotation[liveSize];
			for(int i = 0; i < liveSize; i++) {
				CdlFlags cdl = cdlFlags[i];
				ByteType type = ByteType.Unreached;
				if((cdl & CdlFlags.Code) != 0)       type = ByteType.Opcode;
				else if((cdl & CdlFlags.Data) != 0)  type = ByteType.Data8;

				InOutPoint flow = InOutPoint.None;
				if((cdl & CdlFlags.SubEntryPoint) != 0) flow = InOutPoint.InPoint;

				bytes[i] = new ByteAnnotation {
					Type  = type,
					Flow  = flow,
					XFlag = (cdl & CdlFlags.IndexMode8)  != 0,
					MFlag = (cdl & CdlFlags.MemoryMode8) != 0,
				};
			}

			// Convert LabelManager SnesPrgRom labels (ROM offset) → SNES address dicts.
			var labelDict        = new Dictionary<int, string>();
			var labelCommentDict = new Dictionary<int, string>();
			var commentDict      = new Dictionary<int, string>();

			foreach(CodeLabel cl in LabelManager.GetAllLabels()) {
				if(cl.MemoryType != MemoryType.SnesPrgRom) continue;
				if(!SnesAddressConverter.TryToSnesAddress((int)cl.Address, liveSize, mapMode, out int snesAddr)) continue;

				if(cl.Label.Length > 0) {
					labelDict[snesAddr] = cl.Label;
					if(cl.Comment.Length > 0)
						labelCommentDict[snesAddr] = cl.Comment;
				} else if(cl.Comment.Length > 0) {
					commentDict[snesAddr] = cl.Comment;
				}
			}

			return new RomAnnotationStore {
				RomGameName   = gameName,
				RomChecksum   = checksum,
				MapMode       = mapMode,
				Speed         = speed,
				SaveVersion   = 104,
				Bytes         = bytes,
				Labels        = labelDict,
				LabelComments = labelCommentDict,
				Comments      = commentDict,
			};
		}

		// ── SNES header detection ─────────────────────────────────────────────

		// The SNES internal header sits at a fixed ROM offset depending on map mode.
		// We try both candidate locations and pick the one whose checksum validates.
		//
		// Header layout (relative to title start):
		//   +0x00  title (21 bytes, ASCII space-padded)
		//   +0x15  map mode byte
		//   +0x1C  checksum complement (2 bytes LE)
		//   +0x1E  checksum (2 bytes LE)
		//
		// Validation: (checksum + complement) & 0xFFFF == 0xFFFF

		private static void TryDetectSnesHeader(byte[] rom,
			out RomMapMode mapMode, out RomSpeed speed, out string gameName, out uint dizChecksum)
		{
			// Default fallback.
			mapMode     = RomMapMode.LoRom;
			speed       = RomSpeed.SlowRom;
			gameName    = "";
			dizChecksum = 0;

			bool loValid = TryReadHeader(rom, 0x7FC0, out var lo);
			bool hiValid = TryReadHeader(rom, 0xFFC0, out var hi);

			// Prefer the candidate whose map mode byte matches the header location.
			// LoRom map mode bits 0-3 == 0; HiRom == 1.
			if(loValid && (!hiValid || (lo.mapModeByte & 0x0F) == 0x00)) {
				Apply(lo, out mapMode, out speed, out gameName, out dizChecksum);
			} else if(hiValid) {
				Apply(hi, out mapMode, out speed, out gameName, out dizChecksum);
			} else if(loValid) {
				Apply(lo, out mapMode, out speed, out gameName, out dizChecksum);
			}
		}

		private static void Apply(
			(string title, byte mapModeByte, uint dizChecksum) h,
			out RomMapMode mapMode, out RomSpeed speed, out string gameName, out uint dizChecksum)
		{
			gameName    = h.title;
			speed       = (h.mapModeByte & 0x10) != 0 ? RomSpeed.FastRom : RomSpeed.SlowRom;
			mapMode     = MapModeFromByte(h.mapModeByte);
			dizChecksum = h.dizChecksum;
		}

		// Match Mesen's detection logic from BaseCartridge.cpp LoadRom().
		private static RomMapMode MapModeFromByte(byte b)
		{
			if((b & 0x27) == 0x25) return RomMapMode.ExHiRom;   // 0x25 or 0x35
			if((b & 0x27) == 0x22) return RomMapMode.ExLoRom;   // 0x22 or 0x32
			return (b & 0x0F) switch {
				0x01 => RomMapMode.HiRom,
				0x03 => RomMapMode.Sa1Rom,
				_    => RomMapMode.LoRom,
			};
		}

		private static bool TryReadHeader(byte[] rom, int titleOffset,
			out (string title, byte mapModeByte, uint dizChecksum) result)
		{
			result = default;
			int end = titleOffset + 0x20; // need at least 32 bytes past title start
			if(rom.Length < end) return false;

			ushort complement = (ushort)(rom[titleOffset + 0x1C] | rom[titleOffset + 0x1D] << 8);
			ushort checksum   = (ushort)(rom[titleOffset + 0x1E] | rom[titleOffset + 0x1F] << 8);
			if(((complement + checksum) & 0xFFFF) != 0xFFFF) return false;

			// DiztinGUIsh InternalCheckSum = (checksumWord << 16) | complementWord
			uint dizChecksum = ((uint)checksum << 16) | complement;

			// Decode ASCII title (trim trailing spaces/nulls).
			var sb = new StringBuilder(21);
			for(int i = 0; i < 21; i++) {
				byte b = rom[titleOffset + i];
				if(b == 0) break;
				sb.Append(b >= 0x20 && b < 0x80 ? (char)b : '?');
			}
			result = (sb.ToString().TrimEnd(), rom[titleOffset + 0x15], dizChecksum);
			return true;
		}

		// ── File I/O ──────────────────────────────────────────────────────────

		private static string ReadDizFile(string path)
		{
			if(Path.GetExtension(path).Equals("." + FileDialogHelper.DizExt, StringComparison.OrdinalIgnoreCase)) {
				using var fs     = File.OpenRead(path);
				using var gz     = new GZipStream(fs, CompressionMode.Decompress);
				using var reader = new StreamReader(gz, Encoding.UTF8);
				return reader.ReadToEnd();
			}
			return File.ReadAllText(path, Encoding.UTF8);
		}

		private static void WriteDizFile(string path, string xml)
		{
			var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
			if(Path.GetExtension(path).Equals("." + FileDialogHelper.DizExt, StringComparison.OrdinalIgnoreCase)) {
				using var fs = File.Create(path);
				using var gz = new GZipStream(fs, CompressionLevel.Optimal);
				using var w  = new StreamWriter(gz, utf8NoBom);
				w.Write(xml);
			} else {
				File.WriteAllText(path, xml, utf8NoBom);
			}
		}
	}
}
