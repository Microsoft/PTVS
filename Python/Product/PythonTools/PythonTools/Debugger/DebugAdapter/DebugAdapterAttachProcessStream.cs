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
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Microsoft.PythonTools.Debugger {
    internal sealed class DebugAdapterAttachProcessStream : Stream {
        private readonly NetworkStream _networkStream;
        public DebugAdapterAttachProcessStream(NetworkStream networkStream) {
            _networkStream = networkStream;
        }

        public override bool CanRead => _networkStream.CanRead;

        public override bool CanSeek => _networkStream.CanSeek;

        public override bool CanWrite => _networkStream.CanWrite;

        public override long Length => _networkStream.Length;

        public override long Position {
            get => _networkStream.Position;
            set => _networkStream.Position = value;
        }

        public override void Flush() => _networkStream.Flush();

        public override int Read(byte[] buffer, int offset, int count) {
            try {
                var bytes = _networkStream.Read(buffer, offset, count);
                CheckForResponse(buffer, bytes);
                return bytes;
            } catch (IOException ex) when (IsExpectedError(ex.InnerException as SocketException)) {
                // This is a case where the debuggee has exited, but the adapter host attempts to read remaining messages.
                // Returning 0 here will tell the debugger that the stream is empty.
            }
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => _networkStream.Seek(offset, origin);

        public override void SetLength(long value) => _networkStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) {
            try {
                _networkStream.Write(buffer, offset, count);
            } catch (IOException ex) when (IsExpectedError(ex.InnerException as SocketException)) {
                // This is a case where the debuggee has exited, but the adapter host attempts to write to it.
                Debug.WriteLine($"Attempt to write after stream is closed.", nameof(DebugAdapterAttachProcessStream));
            }
        }

        protected override void Dispose(bool disposing) {
            _networkStream.Dispose();
            base.Dispose(disposing);
        }

        private static bool IsExpectedError(SocketException ex) 
            => ex?.SocketErrorCode == SocketError.ConnectionReset || ex?.SocketErrorCode == SocketError.ConnectionAborted;

        private void CheckForResponse(byte[] buffer, int count) {
            if (Disconnected == null && Initialized == null) {
                return;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, count);
            var splitter = new string[] { "\r", "\n" };
            var jsonBlocks = text
                .Replace("Content-Length", "\r\nContent-Length")
                .Split(splitter, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s.StartsWith("{") && s.EndsWith("}"));
            foreach (var jsonBlock in jsonBlocks) {
                try {
                    var json = JObject.Parse(jsonBlock);
                    if (!json.TryGetValue("command", out var tok)) {
                        json.TryGetValue("event", out tok);
                    }
                    var name = tok.Value<string>();
                    if (name == "disconnect") {
                        Task.Factory.StartNew(async () => {
                            await Task.Delay(50);
                            Disconnected?.Invoke(this, null);
                        });
                    } else if (name == "initialized") {
                        Task.Factory.StartNew(async () => {
                            Initialized?.Invoke(this, null);
                        });
                    } else if (name.StartsWith("legacy")) {
                        if (_legacyDebuggerEventCount == 0) {
                            Task.Factory.StartNew(async () => {
                                LegacyDebugger?.Invoke(this, null);
                            });
                        }
                        _legacyDebuggerEventCount++;
                    }
                } catch (Exception) {
                    // Ignore all parse errors
                }
            }
        }

        public event EventHandler Initialized;
        public event EventHandler Disconnected;
        public event EventHandler LegacyDebugger;
        private int _legacyDebuggerEventCount;
    }
}
