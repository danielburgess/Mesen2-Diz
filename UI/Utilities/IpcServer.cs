using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mesen.Config;
using Mesen.Interop;

namespace Mesen.Utilities
{
	public class IpcServer : IDisposable
	{
		public const string DefaultPipeName = "Mesen2Diz_DebuggerIpc";

		private CancellationTokenSource? _cts;
		private Task? _listenTask;
		private bool _disposed;
		private string _pipeName;

		private static IpcServer? _instance;
		public static IpcServer? Instance => _instance;
		public static string CurrentPipeName => _instance?._pipeName ?? DefaultPipeName;

		/// <summary>
		/// Fired after every IPC command is handled. Args: command, rawRequest, rawResponse, success.
		/// </summary>
		public static event Action<string, string, string, bool>? CommandReceived;

		public static void Start(string? romName = null)
		{
			Stop();
			_instance = new IpcServer(ResolvePipeName(romName));
			_instance.StartListening();
		}

		public static void Stop()
		{
			_instance?.Dispose();
			_instance = null;
		}

		/// <summary>
		/// Called when a ROM is loaded. Only restarts (disconnecting clients)
		/// if the pipe name would actually change AND the user has opted in
		/// via <see cref="Config.IpcConfig.DisconnectOnRomLoad"/>.
		/// A custom pipe name never changes, so no restart is needed.
		/// </summary>
		public static void RestartForRom(string? romName)
		{
			string newName = ResolvePipeName(romName);

			// If the server is already running on the same pipe name, no-op.
			if(_instance != null && _instance._pipeName == newName) {
				return;
			}

			// Custom pipe name set — name never changes, no restart needed.
			if(!string.IsNullOrWhiteSpace(ConfigManager.Config.Debug.Ipc.PipeName)) {
				return;
			}

			// User must opt in to disconnect-on-rom-load.
			if(!ConfigManager.Config.Debug.Ipc.DisconnectOnRomLoad) {
				return;
			}

			Start(romName);
		}

		private static string ResolvePipeName(string? romName)
		{
			string configOverride = ConfigManager.Config.Debug.Ipc.PipeName;
			if(!string.IsNullOrWhiteSpace(configOverride)) {
				return SanitizePipeName(configOverride);
			}
			if(!string.IsNullOrWhiteSpace(romName)) {
				return "Mesen2Diz_" + SanitizePipeName(romName);
			}
			return DefaultPipeName;
		}

		private static string SanitizePipeName(string name)
		{
			return Regex.Replace(Path.GetFileNameWithoutExtension(name), @"[^a-zA-Z0-9_]", "_");
		}

		/// <summary>
		/// Returns the platform-specific path to the named pipe.
		/// Linux: /tmp/CoreFxPipe_{name}
		/// Windows: \\.\pipe\{name}
		/// </summary>
		public static string GetPlatformPipePath(string pipeName)
		{
			if(OperatingSystem.IsWindows()) {
				return @"\\.\pipe\" + pipeName;
			}
			return "/tmp/CoreFxPipe_" + pipeName;
		}

		private IpcServer(string pipeName)
		{
			_pipeName = pipeName;
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
						_pipeName,
						PipeDirection.InOut,
						NamedPipeServerStream.MaxAllowedServerInstances,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous
					);

					await server.WaitForConnectionAsync(ct);
					_ = Task.Run(() => HandleConnection(server, ct), ct);
				} catch(OperationCanceledException) {
					server?.Dispose();
					break;
				} catch(Exception ex) {
					Console.WriteLine($"[IPC] Listen error: {ex.Message}");
					server?.Dispose();
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

					while(server.IsConnected && !ct.IsCancellationRequested) {
						string? line = await reader.ReadLineAsync();
						if(line == null) break;
						if(string.IsNullOrWhiteSpace(line)) continue;

						string command = "?";
						bool success = false;
						string response;
						try {
							// Extract command name for logging
							var parsed = JsonNode.Parse(line);
							command = parsed?["command"]?.GetValue<string>() ?? "?";
							response = IpcCommandHandler.HandleCommand(line);
							var respNode = JsonNode.Parse(response);
							success = respNode?["success"]?.GetValue<bool>() ?? false;
						} catch(Exception ex) {
							response = new JsonObject {
								["success"] = false,
								["error"] = $"Internal error: {ex.Message}"
							}.ToJsonString();
						}

						CommandReceived?.Invoke(command, line, response, success);
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
			try {
				using var dummy = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
				dummy.Connect(200);
			} catch { }

			try { _listenTask?.Wait(1000); } catch { }
			_cts?.Dispose();
		}
	}
}
