using ReactiveUI.Fody.Helpers;

namespace Mesen.Config
{
	public class IpcConfig
	{
		/// <summary>
		/// Override pipe name. Empty = auto from ROM name (Mesen2Diz_{RomBaseName}).
		/// </summary>
		[Reactive] public string PipeName { get; set; } = "";

		/// <summary>
		/// Enable/disable the IPC server.
		/// </summary>
		[Reactive] public bool Enabled { get; set; } = true;

		/// <summary>
		/// When true and no custom pipe name is set, loading a different ROM
		/// forces a pipe name change and disconnects connected clients.
		/// When false (default), the server keeps the existing pipe open.
		/// </summary>
		[Reactive] public bool DisconnectOnRomLoad { get; set; } = false;

		/// <summary>
		/// SPSC ring buffer size for the native MMIO callback hook (events).
		/// Must be a power of two. Rounded up if not, clamped to [1024, 4194304].
		/// Bigger = tolerates larger poll-cadence gaps without dropping events.
		/// </summary>
		[Reactive] public uint MemoryWatchRingSize { get; set; } = 65536;
	}
}
