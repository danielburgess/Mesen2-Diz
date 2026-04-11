using Mesen.Config;
using Mesen.Interop;
using System;
using System.IO;

namespace Mesen.Debugger.Utilities
{
	public static class RamDumper
	{
		private static readonly (MemoryType Type, string Suffix)[] SnesMemoryTypes = new[]
		{
			(MemoryType.SnesWorkRam,   "wram"),
			(MemoryType.SnesSaveRam,   "sram"),
			(MemoryType.SnesVideoRam,  "vram"),
			(MemoryType.SnesSpriteRam, "oam"),
			(MemoryType.SnesCgRam,     "cgram"),
			(MemoryType.SpcRam,        "spcram"),
		};

		public static void DumpIfEnabled(CpuType cpuType)
		{
			DebuggerConfig cfg = ConfigManager.Config.Debug.Debugger;
			if(!cfg.AutoDumpRamOnPause) {
				return;
			}

			// Only SNES supported for now
			if(cpuType != CpuType.Snes && cpuType != CpuType.Spc) {
				return;
			}

			string romName = GetCurrentRomName();
			if(string.IsNullOrWhiteSpace(romName)) {
				romName = "rom";
			}

			string folder = GetEffectiveFolder(cfg, romName);
			if(string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
				return;
			}

			try {
				string screenshotPath = Path.Combine(folder, $"{romName}_screenshot.png");
				EmuApi.TakeScreenshotToFile(screenshotPath);
			} catch(Exception) {
				// Silently skip failed screenshot
			}

			foreach(var (memType, suffix) in SnesMemoryTypes) {
				int size = DebugApi.GetMemorySize(memType);
				if(size <= 0) {
					continue;
				}

				try {
					byte[] data = DebugApi.GetMemoryState(memType);
					string path = Path.Combine(folder, $"{romName}_{suffix}.dmp");
					File.WriteAllBytes(path, data);
				} catch(Exception) {
					// Silently skip failed dumps
				}
			}
		}

		public static string GetCurrentRomName()
		{
			try {
				string name = SanitizeFileName(EmuApi.GetRomInfo().GetRomName());
				return string.IsNullOrWhiteSpace(name) ? "" : name;
			} catch {
				return "";
			}
		}

		public static string GetEffectiveFolder(DebuggerConfig cfg, string romName)
		{
			if(!string.IsNullOrWhiteSpace(romName) &&
				cfg.RamDumpFolderOverrides.TryGetValue(romName, out string? overrideFolder) &&
				!string.IsNullOrWhiteSpace(overrideFolder)) {
				return overrideFolder;
			}
			return cfg.RamDumpFolder;
		}

		private static string SanitizeFileName(string name)
		{
			char[] invalid = Path.GetInvalidFileNameChars();
			foreach(char c in invalid) {
				name = name.Replace(c, '_');
			}
			return name.Trim();
		}
	}
}
