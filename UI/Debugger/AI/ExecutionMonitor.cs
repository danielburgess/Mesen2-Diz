using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Mesen.Debugger.Labels;
using Mesen.Interop;

namespace Mesen.Debugger.AI
{
	public enum ReviewQueueItemSource { Monitor, AiRequested }

	public class ReviewQueueItem
	{
		public uint CpuAddress { get; set; }
		public uint RomOffset { get; set; }
		public CdlFlags Flags { get; set; }
		public string Reason { get; set; } = "";
		public ReviewQueueItemSource Source { get; set; }
		public DateTime DiscoveredAt { get; set; } = DateTime.Now;
		public bool IsAnalyzed { get; set; }

		public string AddressDisplay => $"${CpuAddress:X6}";
		public string FlagsDisplay {
			get {
				if((Flags & CdlFlags.SubEntryPoint) != 0) return "SubEntry";
				if((Flags & CdlFlags.JumpTarget) != 0) return "JumpTarget";
				return Source == ReviewQueueItemSource.AiRequested ? "AI" : "Monitor";
			}
		}
	}

	/// <summary>
	/// Monitors CDL data on each PPU frame boundary, detects newly-executed
	/// unannotated branch/sub-entry targets, and queues them for AI review.
	/// Also fires when a user breakpoint is hit.
	/// </summary>
	public class ExecutionMonitor : IDisposable
	{
		private readonly NotificationListener _listener;
		private CdlFlags[]? _previousCdl;
		private bool _monitoring;

		public ObservableCollection<ReviewQueueItem> Queue { get; } = new();

		/// <summary>Fires when a new item is added to the queue.</summary>
		public event Action<ReviewQueueItem>? OnNewItem;

		/// <summary>
		/// Fires when a user breakpoint is hit (BreakSource.Breakpoint only).
		/// Raised on the notification thread — dispatch to UI thread before touching UI.
		/// </summary>
		public event Action<BreakEvent, uint>? OnBreakpointHit;

		public ExecutionMonitor()
		{
			_listener = new NotificationListener();
			_listener.OnNotification += OnNotification;
		}

		public bool IsMonitoring => _monitoring;

		public void Start()
		{
			_previousCdl = null;
			_monitoring = true;
		}

		public void Stop()
		{
			_monitoring = false;
		}

		public void Reset()
		{
			_previousCdl = null;
			Queue.Clear();
		}

		public void AddToQueue(ReviewQueueItem item)
		{
			// Avoid duplicates
			foreach(var existing in Queue)
				if(existing.CpuAddress == item.CpuAddress) return;

			Queue.Add(item);
			OnNewItem?.Invoke(item);
		}

		private void OnNotification(NotificationEventArgs e)
		{
			if(e.NotificationType == ConsoleNotificationType.CodeBreak) {
				var evt = Marshal.PtrToStructure<BreakEvent>(e.Parameter);
				if(evt.Source == BreakSource.Breakpoint && evt.SourceCpu == CpuType.Snes) {
					uint pc = (uint)DebugApi.GetProgramCounter(CpuType.Snes, true);
					OnBreakpointHit?.Invoke(evt, pc);
				}
				return;
			}

			if(!_monitoring) return;
			if(e.NotificationType != ConsoleNotificationType.PpuFrameDone) return;

			int romSize = DebugApi.GetMemorySize(MemoryType.SnesPrgRom);
			if(romSize <= 0) return;

			var currentCdl = DebugApi.GetCdlData(0, (uint)romSize, MemoryType.SnesPrgRom);

			if(_previousCdl != null && _previousCdl.Length == currentCdl.Length) {
				for(int i = 0; i < currentCdl.Length; i++) {
					var prev = _previousCdl[i];
					var curr = currentCdl[i];

					// Newly covered as code at a branch or sub-entry target
					bool newlyCode = (curr & CdlFlags.Code) != 0 && (prev & CdlFlags.Code) == 0;
					bool isTarget = (curr & (CdlFlags.JumpTarget | CdlFlags.SubEntryPoint)) != 0;

					if(newlyCode && isTarget) {
						var absAddr = new AddressInfo { Address = i, Type = MemoryType.SnesPrgRom };
						if(LabelManager.GetLabel(absAddr) == null) {
							// Convert ROM offset to SNES CPU address
							var relAddr = DebugApi.GetRelativeAddress(absAddr, CpuType.Snes);
							uint cpuAddr = relAddr.Address >= 0 ? (uint)relAddr.Address : 0;

							AddToQueue(new ReviewQueueItem {
								CpuAddress = cpuAddr,
								RomOffset = (uint)i,
								Flags = curr,
								Source = ReviewQueueItemSource.Monitor
							});
						}
					}
				}
			}

			_previousCdl = currentCdl;
		}

		public void Dispose()
		{
			_listener.OnNotification -= OnNotification;
			_listener.Dispose();
		}
	}
}
