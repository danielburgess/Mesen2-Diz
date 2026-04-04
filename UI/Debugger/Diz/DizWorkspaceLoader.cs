using Avalonia.Controls;
using Mesen.Annotation;
using Mesen.Annotation.Asm;
using Mesen.Annotation.Diz;
using Mesen.Config;
using Mesen.Debugger.Labels;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Utilities;
using Mesen.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

		public static void LoadFile(string path, bool showResult, bool overwriteExisting = true, Window? owner = null)
		{
			string xml;
			try {
				xml = ReadDizFile(path);
			} catch(Exception ex) {
				MesenMsgBox.Show(owner, "DizLoadError", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.Message);
				return;
			}

			RomAnnotationStore store;
			try {
				store = DizProjectImporter.Import(xml);
			} catch(Exception ex) {
				MesenMsgBox.Show(owner, "DizLoadError", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.Message);
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
							if(!overwriteExisting) {
								// Skip labels where a non-empty label already exists.
								CodeLabel? existing = LabelManager.GetLabel(label.Address, label.MemoryType);
								if(existing != null && existing.Label.Length > 0) continue;
							}
							labels.Add(label);
						}
					}
				}
			}

			LabelManager.SetLabels(labels);

			// Keep store alive for export.
			CurrentStore = store;

			if(showResult) {
				MesenMsgBox.Show(owner,
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

			// Always refresh labels from the live LabelManager so that any labels
			// added/changed since CurrentStore was first built are included.
			BuildLabelDicts(liveSize, updated.MapMode,
				out var labels, out var labelComments, out var comments);
			updated = new RomAnnotationStore {
				RomGameName   = updated.RomGameName,
				RomChecksum   = updated.RomChecksum,
				MapMode       = updated.MapMode,
				Speed         = updated.Speed,
				SaveVersion   = updated.SaveVersion,
				Bytes         = updated.Bytes,
				Labels        = labels,
				LabelComments = labelComments,
				Comments      = comments,
			};

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

		/// <summary>
		/// Merge live CDL data into the current store (or build one from scratch),
		/// disassemble the ROM, and write a split multi-file export to
		/// <paramref name="directory"/>: a main file, a label definitions file, and
		/// one per-bank code/data file, all named using <paramref name="baseName"/>
		/// as a stem.
		/// </summary>
		public static void ExportAsmFileSplit(string directory, string baseName)
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

			BuildLabelDicts(liveSize, updated.MapMode,
				out var labels, out var labelComments, out var comments);
			updated = new RomAnnotationStore {
				RomGameName   = updated.RomGameName,
				RomChecksum   = updated.RomChecksum,
				MapMode       = updated.MapMode,
				Speed         = updated.Speed,
				SaveVersion   = updated.SaveVersion,
				Bytes         = updated.Bytes,
				Labels        = labels,
				LabelComments = labelComments,
				Comments      = comments,
			};

			CurrentStore = updated;

			byte[] romBytes = DebugApi.GetMemoryState(memType);

			try {
				SnesAsmSplitResult result = SnesAsmExporter.ExportSplit(updated, romBytes, baseName);
				var utf8NoBom = new System.Text.UTF8Encoding(false);
				File.WriteAllText(Path.Combine(directory, $"{baseName}_main.asm"),   result.MainFile,   utf8NoBom);
				File.WriteAllText(Path.Combine(directory, $"{baseName}_labels.asm"), result.LabelsFile, utf8NoBom);
				foreach(var (bank, content) in result.BankFiles)
					File.WriteAllText(Path.Combine(directory, $"{baseName}_bank{bank:X2}.asm"), content, utf8NoBom);
			} catch(Exception ex) {
				MesenMsgBox.Show(null, "DizExportError", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.Message);
				return;
			}

			MesenMsgBox.Show(null, "DizExportSuccess", MessageBoxButtons.OK, MessageBoxIcon.Info,
				$"{baseName}_main.asm");
		}

		// ── Synthetic branch label generation ────────────────────────────────

		/// <summary>
		/// Computes CodeLabel entries for every BRA/BRL branch target that has no
		/// label in LabelManager. Returns an empty list if no ROM is loaded.
		/// </summary>
		public static List<CodeLabel> ComputeMissingSyntheticBranchLabels()
		{
			MemoryType memType  = MemoryType.SnesPrgRom;
			int        liveSize = DebugApi.GetMemorySize(memType);
			if(liveSize <= 0) return new List<CodeLabel>();

			// Build a store the same way ExportAsmFile does.
			RomAnnotationStore? store = CurrentStore ?? BuildStoreFromMesen(memType, liveSize);
			if(store == null) return new List<CodeLabel>();

			if(liveSize > 0) {
				CdlFlags[] cdlFlags = DebugApi.GetCdlData(0, (uint)liveSize, memType);
				byte[]     cdlBytes = new byte[cdlFlags.Length];
				for(int i = 0; i < cdlFlags.Length; i++) cdlBytes[i] = (byte)cdlFlags[i];
				store = DizToMesenAdapter.MergeFromCdlData(store, cdlBytes);
			}

			byte[] romBytes = DebugApi.GetMemoryState(memType);

			// Build ROM-offset keyed dict of labels currently in LabelManager.
			BuildLabelDicts(liveSize, store.MapMode,
				out var labels, out _, out _);
			var existingByOffset = new Dictionary<int, string>(labels.Count);
			foreach(var (snesAddr, name) in labels) {
				if(SnesAddressConverter.TryToRomOffset(snesAddr, store.MapMode, out int off))
					existingByOffset[off] = name;
			}

			return SnesAsmExporter.FindUnlabeledBranchTargets(store, romBytes, existingByOffset)
				.Select(t => new CodeLabel {
					Address    = (uint)t.romOffset,
					MemoryType = memType,
					Label      = t.name,
					Comment    = "",
					Length     = 1,
				})
				.ToList();
		}

		/// <summary>
		/// Adds <paramref name="labels"/> to LabelManager and auto-saves the workspace.
		/// </summary>
		public static void CreateSyntheticBranchLabels(IReadOnlyList<CodeLabel> labels)
		{
			if(labels.Count == 0) return;
			var safe = labels.Where(l => {
				CodeLabel? existing = LabelManager.GetLabel(l.Address, l.MemoryType);
				return existing == null || existing.Label.Length == 0;
			}).ToList();
			if(safe.Count == 0) return;
			LabelManager.SetLabels(safe, raiseEvents: true);
			DebugWorkspaceManager.AutoSave();
		}

		/// <summary>
		/// Returns a CodeLabel for every CDL SubEntryPoint address (JSR/JSL call target)
		/// that has no label in LabelManager. Returns an empty list if no ROM is loaded.
		/// Labels are named "sub_XXXXXX" using the absolute ROM offset as hex.
		/// </summary>
		public static List<CodeLabel> ComputeMissingCdlFunctionLabels()
		{
			MemoryType memType  = MemoryType.SnesPrgRom;
			int        liveSize = DebugApi.GetMemorySize(memType);
			if(liveSize <= 0) return new List<CodeLabel>();

			UInt32[] functions = DebugApi.GetCdlFunctions(memType);
			var result = new List<CodeLabel>(functions.Length);
			foreach(UInt32 offset in functions) {
				if(offset >= (uint)liveSize) continue;
				var addrInfo = new AddressInfo() { Address = (int)offset, Type = memType };
				CodeLabel? existing = LabelManager.GetLabel(addrInfo);
				if(existing != null && existing.Label.Length > 0) continue;
				string name = "sub_" + offset.ToString("X6");
				// Ensure name doesn't collide with an existing label at a different address.
				if(LabelManager.GetLabel(name) != null) continue;
				result.Add(new CodeLabel {
					Address    = offset,
					MemoryType = memType,
					Label      = name,
					Comment    = "",
					Length     = 1,
				});
			}
			return result;
		}

		/// <summary>
		/// Adds CDL-derived function labels to LabelManager and auto-saves the workspace.
		/// </summary>
		public static void CreateCdlFunctionLabels(IReadOnlyList<CodeLabel> labels)
		{
			if(labels.Count == 0) return;
			var safe = labels.Where(l => {
				CodeLabel? existing = LabelManager.GetLabel(l.Address, l.MemoryType);
				return existing == null || existing.Label.Length == 0;
			}).ToList();
			if(safe.Count == 0) return;
			LabelManager.SetLabels(safe, raiseEvents: true);
			DebugWorkspaceManager.AutoSave();
		}

		// ── Build label dictionaries from live LabelManager ──────────────────
		// Accepts any memory type (SnesPrgRom or SnesMemory) and normalises to a
		// SNES CPU address via GetAbsoluteAddress so that UI-set labels are included.

		private static void BuildLabelDicts(int liveSize, RomMapMode mapMode,
			out Dictionary<int, string> labels,
			out Dictionary<int, string> labelComments,
			out Dictionary<int, string> comments)
		{
			labels        = new Dictionary<int, string>();
			labelComments = new Dictionary<int, string>();
			comments      = new Dictionary<int, string>();

			var seen = new HashSet<int>();
			foreach(CodeLabel cl in LabelManager.GetAllLabels()) {
				// Normalise to PRG ROM offset, accepting labels from any address space.
				AddressInfo abs = cl.MemoryType == MemoryType.SnesPrgRom
					? new AddressInfo { Address = (int)cl.Address, Type = MemoryType.SnesPrgRom }
					: DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)cl.Address, Type = cl.MemoryType });
				if(abs.Type != MemoryType.SnesPrgRom || abs.Address < 0) continue;
				if(!seen.Add(abs.Address)) continue;

				if(!SnesAddressConverter.TryToSnesAddress(abs.Address, liveSize, mapMode, out int snesAddr)) continue;

				string commentText = cl.Comment;
				if(cl.Category != Mesen.Config.FunctionCategory.None) {
					string prefix = $"[{cl.Category}] ";
					commentText = commentText.Length > 0 ? prefix + commentText : prefix.TrimEnd();
				}

				if(cl.Label.Length > 0) {
					labels[snesAddr] = cl.Label;
					if(commentText.Length > 0)
						labelComments[snesAddr] = commentText;
				} else if(commentText.Length > 0) {
					comments[snesAddr] = commentText;
				}
			}
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
			// NOTE: Mesen's CDL sets Code on ALL bytes fetched during execution —
			// both opcodes and their operands.  We initially mark everything Code
			// as Opcode, then do a second instruction-walk pass to re-mark the
			// operand bytes correctly so FindUnlabeledBranchTargets doesn't produce
			// spurious labels from operand bytes whose values look like BRA/BRL.
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

			// Second pass: walk from SubEntryPoint / JumpTarget seeds to fix
			// operand bytes that CDL mis-marks as Code (and therefore Opcode).
			FixupOperandBytes(bytes, romBytes, cdlFlags, liveSize);

			BuildLabelDicts(liveSize, mapMode,
				out var labelDict, out var labelCommentDict, out var commentDict);

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

		// ── Operand byte fixup ────────────────────────────────────────────────

		/// <summary>
		/// Walks the instruction stream from known entry points (SubEntryPoint and
		/// JumpTarget CDL seeds) and marks operand bytes as <see cref="ByteType.Operand"/>.
		///
		/// Mesen's CDL sets <see cref="CdlFlags.Code"/> on every byte the CPU fetched,
		/// including operand bytes.  Without this pass every Code byte would be typed
		/// as Opcode, causing <c>FindUnlabeledBranchTargets</c> to treat operand bytes
		/// whose value happens to be a BRA/BRL opcode as real branch instructions and
		/// produce thousands of spurious CODE_ labels.
		/// </summary>
		private static void FixupOperandBytes(
			ByteAnnotation[] bytes, byte[] romBytes, CdlFlags[] cdl, int len)
		{
			var visited  = new HashSet<int>(len / 4);
			var worklist = new Queue<int>();

			// Seed from:
			//  1. SubEntryPoint / JumpTarget — CDL-confirmed instruction starts.
			//  2. The first Code byte of every contiguous Code run — these must be
			//     opcode bytes because no preceding Code byte could be their operand.
			//     This covers isolated code regions with no CDL entry-point markers.
			for(int i = 0; i < len; i++) {
				if((cdl[i] & CdlFlags.Code) == 0) continue;
				bool isMarked  = (cdl[i] & (CdlFlags.SubEntryPoint | CdlFlags.JumpTarget)) != 0;
				bool isRunStart = i == 0 || (cdl[i - 1] & CdlFlags.Code) == 0;
				if(isMarked || isRunStart)
					worklist.Enqueue(i);
			}

			while(worklist.Count > 0) {
				int off = worklist.Dequeue();
				if(off < 0 || off >= len) continue;
				if(!visited.Add(off)) continue;
				if((cdl[off] & CdlFlags.Code) == 0) continue;

				bool m = (cdl[off] & CdlFlags.MemoryMode8) != 0;
				bool x = (cdl[off] & CdlFlags.IndexMode8)  != 0;
				int operands = SnesAsmExporter.GetOperandByteCount(romBytes[off], m, x);

				// Re-mark the operand bytes that CDL incorrectly left as Opcode.
				for(int j = 1; j <= operands; j++) {
					int opOff = off + j;
					if(opOff >= len) break;
					if((cdl[opOff] & CdlFlags.Code) != 0)
						bytes[opOff] = bytes[opOff] with { Type = ByteType.Operand };
					visited.Add(opOff);
				}

				// Follow sequential fall-through to the next instruction.
				int next = off + 1 + operands;
				if(next < len && (cdl[next] & CdlFlags.Code) != 0)
					worklist.Enqueue(next);
			}
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
