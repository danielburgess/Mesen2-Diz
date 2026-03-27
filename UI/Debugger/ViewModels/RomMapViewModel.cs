using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mesen.Annotation;
using Mesen.Config;
using Mesen.Debugger.Controls;
using Mesen.Debugger.Diz;
using Mesen.Debugger.Utilities;
using Mesen.Utilities;
using Mesen.ViewModels;
using Mesen.Windows;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Mesen.Debugger.ViewModels
{
	public class RomMapViewModel : DisposableViewModel
	{
		[Reactive] public DynamicBitmap? ViewerBitmap { get; private set; }
		[Reactive] public string HoverInfo { get; private set; } = "";

		public RomMapViewerConfig Config { get; }
		public List<ContextMenuAction> FileMenuActions { get; } = new();
		public List<ContextMenuAction> ViewMenuActions { get; } = new();

		// Legend entries for the settings panel.
		public IReadOnlyList<LegendEntry> LegendEntries { get; } = BuildLegend();

		private readonly PictureViewer _picViewer;
		private readonly ScrollPictureViewer _scrollViewer;
		private readonly Window _wnd;

		public RomMapViewModel(PictureViewer picViewer, ScrollPictureViewer scrollViewer, Window wnd)
		{
			_picViewer = picViewer;
			_scrollViewer = scrollViewer;
			_wnd = wnd;

			Config = ConfigManager.Config.Debug.RomMapViewer;

			FileMenuActions.AddRange(new[] {
				new ContextMenuAction() {
					ActionType = ActionType.Exit,
					OnClick = () => wnd.Close()
				}
			});

			ViewMenuActions.AddRange(new[] {
				new ContextMenuAction() {
					ActionType = ActionType.ZoomIn,
					OnClick = () => Config.ImageScale = Math.Min(8, Config.ImageScale + 0.5)
				},
				new ContextMenuAction() {
					ActionType = ActionType.ZoomOut,
					OnClick = () => Config.ImageScale = Math.Max(0.25, Config.ImageScale - 0.5)
				},
				new ContextMenuSeparator(),
				new ContextMenuAction() {
					ActionType = ActionType.EnableAutoRefresh,
					IsSelected = () => false,
					IsEnabled = () => false,
				},
			});

			// Re-render when canvas width changes.
			AddDisposable(this.WhenAnyValue(x => x.Config.CanvasWidth).Subscribe(_ => RefreshData()));

			// Wire pointer move → byte info.
			picViewer.PointerMoved += OnPointerMoved;
		}

		public void FitToWidth()
		{
			double available = _scrollViewer.Bounds.Width;
			if(available > 0)
				Config.CanvasWidth = (int)(available / Config.ImageScale);
		}

		public void RefreshData()
		{
			RomAnnotationStore? store = DizWorkspaceLoader.GetOrBuildStore();
			if(store == null) {
				HoverInfo = "No ROM loaded.";
				return;
			}

			int width  = Math.Max(1, Config.CanvasWidth);
			int height = (store.Bytes.Length + width - 1) / width;
			InitBitmap(width, height);

			ByteAnnotation[] bytes = store.Bytes;
			DynamicBitmap bmp = ViewerBitmap!;

			Task.Run(() => {
				using var frameLock = bmp.Lock();
				unsafe {
					UInt32* pixels = (UInt32*)frameLock.FrameBuffer.Address;
					int stride = frameLock.FrameBuffer.RowBytes / sizeof(UInt32);

					for(int i = 0; i < bytes.Length; i++) {
						pixels[(i / width) * stride + (i % width)] = ColorFor(bytes[i].Type);
					}
					// Pad trailing pixels in the last row.
					for(int i = bytes.Length; i < width * height; i++) {
						pixels[(i / width) * stride + (i % width)] = 0xFF000000;
					}
				}
				// DynamicBitmapLock.Dispose() calls bmp.Invalidate() automatically.
			});
		}

		private void OnPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
		{
			RomAnnotationStore? store = DizWorkspaceLoader.GetOrBuildStore();
			if(store == null || ViewerBitmap == null) return;

			var pos = _picViewer.GetGridPointFromMousePoint(e.GetPosition(_picViewer));
			if(pos == null) return;

			int x = (int)pos.Value.X;
			int y = (int)pos.Value.Y;
			int width = Math.Max(1, Config.CanvasWidth);
			int offset = y * width + x;

			if(offset < 0 || offset >= store.Bytes.Length) {
				HoverInfo = "";
				return;
			}

			ByteType type = store.Bytes[offset].Type;

			// Try to get the canonical SNES address for extra context.
			string addrStr = "";
			if(SnesAddressConverter.TryToSnesAddress(offset, store.Bytes.Length, store.MapMode, out int snesAddr)) {
				addrStr = $" | SNES: ${snesAddr:X6}";
				if(store.Labels.TryGetValue(snesAddr, out string? label) && label.Length > 0)
					addrStr += $" ({label})";
			}

			HoverInfo = $"Offset: ${offset:X6}{addrStr} | {type}";
		}

		[MemberNotNull(nameof(ViewerBitmap))]
		private void InitBitmap(int width, int height)
		{
			if(ViewerBitmap == null
				|| ViewerBitmap.PixelSize.Width  != width
				|| ViewerBitmap.PixelSize.Height != height) {
				ViewerBitmap = new DynamicBitmap(
					new PixelSize(width, height),
					new Vector(96, 96),
					PixelFormat.Bgra8888,
					AlphaFormat.Premul);
			}
		}

		// ── Colors ────────────────────────────────────────────────────────────
		// UInt32 format: 0xAARRGGBB (premultiplied; alpha=FF → straight is identical).

		private static UInt32 ColorFor(ByteType type) => type switch {
			ByteType.Opcode    => 0xFF5080FF,
			ByteType.Operand   => 0xFF80A8FF,
			ByteType.Data8     => 0xFFFF8020,
			ByteType.Data16    => 0xFFFFA000,
			ByteType.Data24    => 0xFFFF6010,
			ByteType.Data32    => 0xFFD01010,
			ByteType.Pointer16 => 0xFFFFD000,
			ByteType.Pointer24 => 0xFFFF3010,
			ByteType.Pointer32 => 0xFFA00000,
			ByteType.Graphics  => 0xFF40C040,
			ByteType.Music     => 0xFFC050C0,
			ByteType.Text      => 0xFF00C0A0,
			ByteType.Empty     => 0xFF101010,
			_                  => 0xFF282828,  // Unreached and anything unknown
		};

		private static IReadOnlyList<LegendEntry> BuildLegend()
		{
			var entries = new List<LegendEntry>();
			foreach(ByteType t in Enum.GetValues<ByteType>()) {
				uint argb = ColorFor(t);
				// Convert 0xAARRGGBB → Avalonia Color (A,R,G,B)
				byte a = (byte)(argb >> 24);
				byte r = (byte)(argb >> 16);
				byte g = (byte)(argb >> 8);
				byte b = (byte)(argb);
				entries.Add(new LegendEntry(t.ToString(), new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(a, r, g, b))));
			}
			return entries;
		}
	}

	public record LegendEntry(string Label, Avalonia.Media.IBrush Brush);
}
