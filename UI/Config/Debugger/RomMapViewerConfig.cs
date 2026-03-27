using ReactiveUI.Fody.Helpers;

namespace Mesen.Config
{
	public class RomMapViewerConfig : BaseWindowConfig<RomMapViewerConfig>
	{
		[Reactive] public bool ShowSettingsPanel { get; set; } = true;
		[Reactive] public double ImageScale { get; set; } = 1.0;
		[Reactive] public int CanvasWidth { get; set; } = 512;

		public RomMapViewerConfig()
		{
		}
	}
}
