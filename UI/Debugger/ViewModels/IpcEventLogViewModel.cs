using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using Mesen.Utilities;

namespace Mesen.Debugger.ViewModels
{
	public class IpcEventEntry
	{
		public DateTime Timestamp { get; init; }
		public string Command { get; init; } = "";
		public bool Success { get; init; }
		public string Request { get; init; } = "";
		public string Response { get; init; } = "";

		public string TimeDisplay => Timestamp.ToString("HH:mm:ss.fff");
		public string StatusDisplay => Success ? "OK" : "ERR";

		public IBrush StatusColor => Success ? Brushes.Green : Brushes.Red;
		public string RequestPreview => Request.Length > 120 ? Request[..120] + "..." : Request;
		public string ResponsePreview => Response.Length > 120 ? Response[..120] + "..." : Response;
	}

	public class IpcEventLogViewModel
	{
		private const int MaxEntries = 1000;

		public ObservableCollection<IpcEventEntry> Events { get; } = new();

		public IpcEventLogViewModel()
		{
			IpcServer.CommandReceived += OnCommandReceived;
		}

		private void OnCommandReceived(string command, string request, string response, bool success)
		{
			var entry = new IpcEventEntry {
				Timestamp = DateTime.Now,
				Command = command,
				Success = success,
				Request = request,
				Response = response
			};

			Dispatcher.UIThread.Post(() => {
				Events.Add(entry);
				while(Events.Count > MaxEntries) {
					Events.RemoveAt(0);
				}
			});
		}
	}
}
