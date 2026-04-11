using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Utilities
{
	public class IpcServer : IDisposable
	{
		public const string PipeName = "Mesen2Diz_DebuggerIpc";

		private CancellationTokenSource? _cts;
		private Task? _listenTask;
		private bool _disposed;

		private static IpcServer? _instance;
		public static IpcServer? Instance => _instance;

		public static void Start()
		{
			if(_instance != null) return;
			_instance = new IpcServer();
			_instance.StartListening();
		}

		public static void Stop()
		{
			_instance?.Dispose();
			_instance = null;
		}

		private void StartListening()
		{
			_cts = new CancellationTokenSource();
			_listenTask = Task.Run(() => ListenLoop(_cts.Token));
		}

		private async Task ListenLoop(CancellationToken ct)
		{
			while(!ct.IsCancellationRequested) {
				NamedPipeServerStream? server = null;
				try {
					server = new NamedPipeServerStream(
						PipeName,
						PipeDirection.InOut,
						NamedPipeServerStream.MaxAllowedServerInstances,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous
					);

					await server.WaitForConnectionAsync(ct);
					// Handle each connection on its own task so we can accept the next one immediately
					_ = Task.Run(() => HandleConnection(server, ct), ct);
				} catch(OperationCanceledException) {
					server?.Dispose();
					break;
				} catch(Exception ex) {
					Console.WriteLine($"[IPC] Listen error: {ex.Message}");
					server?.Dispose();
					// Brief pause before retrying
					try { await Task.Delay(500, ct); } catch { break; }
				}
			}
		}

		private async Task HandleConnection(NamedPipeServerStream server, CancellationToken ct)
		{
			try {
				using(server) {
					using var reader = new StreamReader(server, Encoding.UTF8);
					using var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };

					// Read lines until the client disconnects.
					// Protocol: one JSON object per line, one JSON response per line.
					while(server.IsConnected && !ct.IsCancellationRequested) {
						string? line = await reader.ReadLineAsync();
						if(line == null) break; // client disconnected
						if(string.IsNullOrWhiteSpace(line)) continue;

						string response;
						try {
							response = IpcCommandHandler.HandleCommand(line);
						} catch(Exception ex) {
							response = new JsonObject {
								["success"] = false,
								["error"] = $"Internal error: {ex.Message}"
							}.ToJsonString();
						}

						await writer.WriteLineAsync(response);
					}
				}
			} catch(Exception ex) {
				Console.WriteLine($"[IPC] Connection handler error: {ex.Message}");
			}
		}

		public void Dispose()
		{
			if(_disposed) return;
			_disposed = true;

			_cts?.Cancel();
			// Unblock WaitForConnectionAsync by connecting briefly
			try {
				using var dummy = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
				dummy.Connect(200);
			} catch { }

			try { _listenTask?.Wait(1000); } catch { }
			_cts?.Dispose();
		}
	}
}
