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
	}
}
