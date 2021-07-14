// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Debugger.DebugAdapter;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Logging;
using Microsoft.VisualStudio.Debugger.DebugAdapterHost.Interfaces;
using Microsoft.VisualStudio.Debugger.Interop;

namespace Microsoft.PythonTools.Debugger {
    internal sealed class DebugAdapterAttachProcess : ITargetHostProcess, IDisposable {
        private const int _debuggerConnectionTimeout = 60000; // 60 seconds. Can take a long time to connect
        private DebugAdapterAttachProcessStream _stream;
        private bool _debuggerConnected = false;
        private Process _debugPyProcess = null;

        private DebugAdapterAttachProcess(Process debugPyProcess = null) {
            _debugPyProcess = debugPyProcess;
            debugPyProcess.Exited += DebugPyProcess_Exited;
            debugPyProcess.OutputDataReceived += DebugPyProcess_OutputDataReceived;
            debugPyProcess.ErrorDataReceived += DebugPyProcess_OutputDataReceived;
        }

        private void DebugPyProcess_OutputDataReceived(object sender, DataReceivedEventArgs e) {
            Debug.WriteLine($"DebugPy output: {e.Data}");
        }

        private void DebugPyProcess_Exited(object sender, EventArgs e) {
            Debug.WriteLine($"DebugPy exited:");
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public static ITargetHostProcess LocalAttach(DebugAttachInfo debugAttachInfo, IAdapterLaunchInfo launchInfo, ITargetHostInterop targetInterop) {
            // For the local case, we need to start the process ourselves
            AD_PROCESS_ID adProcessId = new AD_PROCESS_ID();
            adProcessId.ProcessIdType = (uint)enum_AD_PROCESS_ID.AD_PROCESS_ID_SYSTEM;
            adProcessId.dwProcessId = (uint)launchInfo.AttachProcessId;
            launchInfo.DebugPort.GetProcess(adProcessId, out var process);
            process.GetName(VisualStudio.Debugger.Interop.enum_GETNAME_TYPE.GN_FILENAME, out var processName);
            var debugPyServerDirectory = Path.GetDirectoryName(PythonToolsInstallPath.GetFile("debugpy\\__init__.py"));
            var port = GetAvailablePort();

            // Start the process using the --listen --pid arguments
            var debugPyProcess = new Process();
            debugPyProcess.StartInfo.FileName = processName;
            debugPyProcess.StartInfo.Arguments = $"\"{debugPyServerDirectory}\" --listen {port} --pid {launchInfo.AttachProcessId}";
            debugPyProcess.StartInfo.RedirectStandardError = true;
            debugPyProcess.StartInfo.RedirectStandardInput = true;
            debugPyProcess.StartInfo.RedirectStandardOutput = true;
            debugPyProcess.StartInfo.UseShellExecute = false;
            debugPyProcess.StartInfo.CreateNoWindow = true;
            debugPyProcess.EnableRaisingEvents = true;
            var attachedProcess = new DebugAdapterAttachProcess(debugPyProcess);
            debugPyProcess.Start();

            // Connect to this port
            return attachedProcess.ConnectSocket(new System.Uri($"http://localhost:{port}"), debugPyProcess) ? attachedProcess : null;
        }

        public static ITargetHostProcess RemoteAttach(DebugAttachInfo debugAttachInfo) {
            var attachedProcess = new DebugAdapterAttachProcess();
            return attachedProcess.ConnectSocket(debugAttachInfo.RemoteUri) ? attachedProcess : null;
        }

        /// <summary>
        /// Gets an available port on the loopback interface.
        /// </summary>
        private static int GetAvailablePort() {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private bool ConnectSocket(Uri uri, Process startupProcess = null) {
            _debuggerConnected = false;
            Socket socket = null; 
            EndPoint endpoint;
            if (uri.IsLoopback) {
                endpoint = new IPEndPoint(IPAddress.Loopback, uri.Port);
            } else {
                endpoint = new DnsEndPoint(uri.Host, uri.Port);
            }

            var logger = (IPythonToolsLogger)VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(IPythonToolsLogger));

            Debug.WriteLine($"Connecting to debugger at {uri.ToString()}");

            // Need an intermediary here so it's the correct type.
            Func<Task> socketTask =
                async () => {
                    // Wait for process exit if we have a 'startup' process
                    if (startupProcess != null) {
                        await startupProcess.WaitForExitAsync(CancellationToken.None);
                    }

                    // Keep looping. debugpy may not be started yet.
                    while (socket == null) {
                        try {
                            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
                            await socket.ConnectAsync(endpoint);
                        } catch (Exception e) {
                            System.Diagnostics.Debug.WriteLine($"Socker error attaching: {e}");
                            socket.Dispose();
                            socket = null;
                            await Task.Delay(1000);
                        }
                    }
                };
            var task = VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(() => Task.WhenAny(
                    socketTask(),
                    Task.Delay(_debuggerConnectionTimeout)));
            try {
                if (socket != null && socket.Connected) {
                    _debuggerConnected = true;
                    _stream = new DebugAdapterAttachProcessStream(new NetworkStream(socket, ownsSocket: true));
                    _stream.Disconnected += OnDisconnected;
                    _stream.Initialized += OnInitialized;
                } else if (task.Exception != null) {
                    Debug.WriteLine($"Error waiting for debugger to connect {task.Exception}");
                } else {
                    Debug.WriteLine("Timed out waiting for debugger to connect.", nameof(DebugAdapterAttachProcess));
                    logger?.LogEvent(PythonLogEvent.DebugAdapterConnectionTimeout, "Attach");
                }
            } catch (AggregateException ex) {
                Debug.WriteLine("Error waiting for debugger to connect {0}".FormatInvariant(ex.InnerException ?? ex), nameof(DebugAdapterAttachProcess));
            }

            return _debuggerConnected;
        }

        private void OnDisconnected(object sender, EventArgs e) {
            if (_stream != null) {
                _stream.Disconnected -= OnDisconnected;
                _stream.Initialized -= OnInitialized;
                _stream.Dispose();
            }
            if (_debugPyProcess != null && !_debugPyProcess.HasExited) { 
                _debugPyProcess.Exited -= DebugPyProcess_Exited;
                _debugPyProcess?.Kill();
            }
            _debugPyProcess = null;
            Exited?.Invoke(this, null);
        }

        private void OnInitialized(object sender, EventArgs e) {
            CustomDebugAdapterProtocolExtension.SendRequest(
                new DebugPyVersionRequest(),
                DebugPyVersionHelper.VerifyDebugPyVersion,
                DebugPyVersionHelper.ShowDebugPyVersionError);
        }

        public IntPtr Handle => IntPtr.Zero;

        public Stream StandardInput => _stream;

        public Stream StandardOutput => _stream;

        public bool HasExited => _debuggerConnected;

        public event EventHandler Exited;
        public event DataReceivedEventHandler ErrorDataReceived;

        public void Dispose() {
            if (_stream != null) {
                _stream.Dispose();
            }
        }

        public void Terminate() {
            if (_stream != null) {
                _stream.Dispose();
            }
            if (_debugPyProcess != null) {
                _debugPyProcess.Kill();
            }

            ErrorDataReceived?.Invoke(this, null);
        }
    }
}
