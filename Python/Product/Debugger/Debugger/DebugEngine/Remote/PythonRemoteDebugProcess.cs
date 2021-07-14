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
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Ipc.Json;
using Microsoft.VisualStudio.Debugger.Interop;

namespace Microsoft.PythonTools.Debugger.Remote {
    internal class PythonRemoteDebugProcess : IDebugProcess2, IDebugProcessSecurity2 {
        private readonly PythonRemoteDebugPort _port;
        private readonly int _pid;
        private readonly string _exe;
        private readonly string _username;
        private readonly string _version;

        public PythonRemoteDebugProcess(PythonRemoteDebugPort port, int pid, string exe, string username, string version) {
            this._port = port;
            this._pid = (pid == 0) ? 1 : pid; // attach dialog won't show processes with pid==0
            this._username = username;
            this._exe = string.IsNullOrEmpty(exe) ? "<python>" : exe;
            this._version = version;
        }

        public PythonRemoteDebugPort DebugPort {
            get { return _port; }
        }

        public int Attach(IDebugEventCallback2 pCallback, Guid[] rgguidSpecificEngines, uint celtSpecificEngines, int[] rghrEngineAttach) {
            throw new NotImplementedException();
        }

        public int CanDetach() {
            return 0; // S_OK = true, S_FALSE = false
        }

        public int CauseBreak() {
            return 0; // S_OK = true, S_FALSE = false
        }

        public int Detach() {
            throw new NotImplementedException();
        }

        public int EnumPrograms(out IEnumDebugPrograms2 ppEnum) {
            ppEnum = new PythonRemoteEnumDebugPrograms(this);
            return 0;
        }

        public int EnumThreads(out IEnumDebugThreads2 ppEnum) {
            throw new NotImplementedException();
        }

        public int GetAttachedSessionName(out string pbstrSessionName) {
            throw new NotImplementedException();
        }

        public int GetInfo(enum_PROCESS_INFO_FIELDS Fields, PROCESS_INFO[] pProcessInfo) {
            // The various string fields should match the strings returned by GetName - keep them in sync when making any changes here.
            var pi = new PROCESS_INFO();
            pi.Fields = Fields;
            pi.bstrFileName = _exe;
            pi.bstrBaseName = BaseName;
            pi.bstrTitle = Title;
            pi.ProcessId.dwProcessId = (uint)_pid;
            pProcessInfo[0] = pi;
            return 0;
        }

        public int GetName(enum_GETNAME_TYPE gnType, out string pbstrName) {
            // The return value should match the corresponding string field returned from GetInfo - keep them in sync when making any changes here.
            switch (gnType) {
                case enum_GETNAME_TYPE.GN_FILENAME:
                    pbstrName = _exe;
                    break;
                case enum_GETNAME_TYPE.GN_BASENAME:
                    pbstrName = BaseName;
                    break;
                case enum_GETNAME_TYPE.GN_NAME:
                case enum_GETNAME_TYPE.GN_TITLE:
                    pbstrName = _version;
                    break;
                default:
                    pbstrName = null;
                    break;
            }
            return 0;
        }

        public int GetPhysicalProcessId(AD_PROCESS_ID[] pProcessId) {
            var pidStruct = new AD_PROCESS_ID();
            pidStruct.dwProcessId = (uint)_pid;
            pProcessId[0] = pidStruct;
            return 0;
        }

        public int GetPort(out IDebugPort2 ppPort) {
            ppPort = _port;
            return 0;
        }

        public int GetProcessId(out Guid pguidProcessId) {
            pguidProcessId = Guid.Empty;
            return 0;
        }

        public int GetServer(out IDebugCoreServer2 ppServer) {
            throw new NotImplementedException();
        }

        public int Terminate() {
            throw new NotImplementedException();
        }

        public int GetUserName(out string pbstrUserName) {
            pbstrUserName = _username;
            return 0;
        }

        public int QueryCanSafelyAttach() {
            return 0; // S_OK = true, S_FALSE = false
        }

        // AzureExplorerAttachDebuggerCommand looks up remote processes by name, and has to be updated if the format of this property changes.
        private string BaseName {
            get {
                string fileName = PathUtils.GetFileOrDirectoryName(_exe);
                if (string.IsNullOrEmpty(fileName)) {
                    fileName = _exe;
                }

                // Strip out the secret to avoid showing it in the process list.
                return fileName + " @ " + new UriBuilder(_port.Uri) { UserName = null };
            }
        }

        private string Title {
            get { return _version; }
        }
    }
}
