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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Parsing;
using Microsoft.PythonTools.Debugger;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Intellisense;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.InteractiveWindow.Commands;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudioTools;

namespace Microsoft.PythonTools.Repl {
    [InteractiveWindowRole("Debug")]
    [ContentType(PythonCoreConstants.ContentType)]
    [ContentType(PredefinedInteractiveCommandsContentTypes.InteractiveCommandContentTypeName)]
    internal class PythonDebugProcessReplEvaluator : PythonInteractiveEvaluator {
        private uint _threadId;
        private Dictionary<string, string> _moduleToFileName;
        private string _currentScopeName;
        private string _currentScopeFileName;

        private string _currentFrameFilename;
        private int _currentProcessId;
        private IDebugProcess2 _currentProcess;
        private const int ExpressionEvaluationTimeout = 3000; // ms

        /// <summary>
        /// Backwards compatible and non-localized name for the scope
        /// that represents execution on the current frame.
        /// </summary>
        private const string CurrentFrameScopeFixedName = "<CurrentFrame>";

        public PythonDebugProcessReplEvaluator(IServiceProvider serviceProvider, IDebugProcess2 process, int pid)
            : base(serviceProvider) {
            _currentScopeName = CurrentFrameScopeFixedName;
            DisplayName = Strings.DebugReplDisplayName;
            _currentProcessId = pid;
            _currentProcess = process;
        }

        public override async Task<ExecutionResult> InitializeAsync() {
            var result = await base.InitializeAsync();
            if (!result.IsSuccessful) {
                return result;
            }

            result = await _serviceProvider.GetUIThread().InvokeTask(async () => {
                UpdatePropertiesFromProjectMoniker();
                var isRemote = DebuggerHelper.IsRemote(_currentProcess);
                try {
                    _serviceProvider.GetPythonToolsService().Logger?.LogEvent(Logging.PythonLogEvent.DebugRepl, new Logging.DebugReplInfo {
                        RemoteProcess = isRemote,
                        Version = "unknown"
                    });
                } catch (Exception ex) {
                    Debug.Fail(ex.ToUnhandledExceptionMessage(GetType()));
                } 

                DebuggerHelper.Instance.ModulesChanged += OnModulesChanged;

                if (DebuggerHelper.Instance.CurrentThread != null) {
                    SwitchThread(DebuggerHelper.Instance.CurrentThread, false);
                }

                return ExecutionResult.Success;
            });

            return result;
        }

        internal override Task InitializeLanguageServerAsync() {
            // Don't run a language server for debug repl, at least for now
            return Task.CompletedTask;
        }

        private async void OnModulesChanged(object sender, EventArgs e) {
            await RefreshAvailableScopes();
        }

        internal async Task<KeyValuePair<string, string>[]> RefreshAvailableScopes() {
            var modules = DebuggerHelper.Instance.GetModuleNamesAndPaths();

            var moduleToFile = new Dictionary<string, string>();
            foreach (var item in modules) {
                if (!string.IsNullOrEmpty(item.Value)) {
                    moduleToFile[item.Key] = item.Value;
                }
            }
            _moduleToFileName = moduleToFile;

            SetAvailableScopes(modules.Select(m => m.Key).ToArray());
            EnableMultipleScopes = true;

            return modules;
        }

        protected override Task ExecuteStartupScripts(string scriptsPath) {
            // Do not execute scripts for debug evaluator
            return Task.FromResult<object>(null);
        }

        public override async Task<ExecutionResult> ExecuteCodeAsync(string text) {
            var tcs = new TaskCompletionSource<ExecutionResult>();
            var ct = new CancellationToken();
            var cancellationRegistration = ct.Register(() => tcs.TrySetCanceled());

            EventHandler processExited = delegate {
                tcs.TrySetCanceled();
            };

            DebuggerHelper.Instance.ProcessExited += processExited;
            try {
                var result = await DebuggerHelper.Instance.EvaluateText(text, ExpressionEvaluationTimeout);
                tcs.TrySetResult(result.Item1);
                return await tcs.Task;
            } finally {
                DebuggerHelper.Instance.ProcessExited -= processExited;
                cancellationRegistration.Dispose();
            }
        }

        public int ProcessId {
            get { return _currentProcessId; }
        }

        public long ThreadId {
            get { return _threadId; }
        }

        public override string CurrentScopeName {
            get {
                // Callers expect the localized name
                return _currentScopeName == CurrentFrameScopeFixedName
                    ? Strings.DebugReplCurrentFrameScope : _currentScopeName;
            }
        }

        public override string CurrentScopePath {
            get {
                return _currentScopeFileName;
            }
        }

        public override string CurrentWorkingDirectory {
            get {
                // This is never called
                return null;
            }
        }

        public override IEnumerable<KeyValuePair<string, string>> GetAvailableScopesAndPaths() {
            var t = RefreshAvailableScopes();
            if (t != null && t.Wait(1000) && t.Result != null) {
                return t.Result;
            }

            return Enumerable.Empty<KeyValuePair<string, string>>();
        }

        public override Task<OverloadDoc[]> GetSignatureDocumentationAsync(string text, CancellationToken ct) {
            // TODO: implement this
            return Task.FromResult(new OverloadDoc[0]);
        }

        private async Task<CompletionResult[]> GetMemberNamesInternalAsync(string text, CancellationToken ct) {
            // TODO: implement support for getting members for module scope,
            // not just for current frame scope
            if (_currentScopeName == CurrentFrameScopeFixedName) {
                var expression = string.Format(CultureInfo.InvariantCulture, "':'.join(dir({0}))", text ?? "");
                var result = await DebuggerHelper.Instance.EvaluateText(expression, ExpressionEvaluationTimeout);
                if (result.Item1.IsSuccessful) {
                    // We don't really know if it's a field, function or else...
                    var completionResults = result.Item2
                        .Split(':')
                        .Where(r => !string.IsNullOrEmpty(r))
                        .Select(r => new CompletionResult(r, PythonMemberType.Generic))
                        .ToArray();

                    return completionResults;
                }
            }

            return Array.Empty<CompletionResult>();
        }

        public override void SetScope(string scopeName) {
            if (!string.IsNullOrWhiteSpace(scopeName)) {
                if (scopeName == Strings.DebugReplCurrentFrameScope) {
                    // Most callers will pass in the localized name, but the $mod command may have either.
                    // Always store the unlocalized name.
                    scopeName = CurrentFrameScopeFixedName;
                }
                _currentScopeName = scopeName;

                if (!(_moduleToFileName?.TryGetValue(scopeName, out _currentScopeFileName) ?? false)) {
                    _currentScopeFileName = null;
                }

                WriteOutput(Strings.ReplModuleChanged.FormatUI(CurrentScopeName));
            } else {
                WriteOutput(CurrentScopeName);
            }
        }
        internal void SwitchThread(long threadId, bool verbose) {
            _currentProcess.EnumThreads(out var ppThreads);
            using (var disposer = new ComDisposer(ppThreads)) {
                ppThreads.GetCount(out var count);
                IDebugThread2[] pThreads = new IDebugThread2[count];
                ppThreads.Next(count, pThreads, ref count);
                disposer.AddRange(pThreads);
                foreach (var pThread in pThreads) {
                    pThread.GetThreadId(out var pThreadId);
                    if (pThreadId == (uint)threadId) {
                        SwitchThread(pThread, verbose);
                        break;
                    }
                }
            }
        }
        
        internal void SwitchThread(IDebugThread2 thread, bool verbose) {
            var frame = DebuggerHelper.GetTopmostFrame(thread);
            if (frame == null) {
                WriteError(Strings.DebugReplCannotChangeCurrentThreadNoFrame.FormatUI(0));
                return;
            }
            thread.GetThreadId(out _threadId);
            frame.GetDebugProperty(out var property);
            using (var disposer = new ComDisposer(property)) {
                DEBUG_PROPERTY_INFO[] propInfoArray = new DEBUG_PROPERTY_INFO[1];
                property.GetPropertyInfo(enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_FULLNAME, 10, 100, null, 0, propInfoArray);
                if (_currentFrameFilename != propInfoArray[0].bstrFullName) {
                    _currentFrameFilename = propInfoArray[0].bstrFullName;
                }
            }
            _currentScopeName = CurrentFrameScopeFixedName;
            _currentScopeFileName = null;
            if (verbose) {
                WriteOutput(Strings.DebugReplThreadChanged.FormatUI(_threadId, 0));
            }
        }

        internal void FrameUp() {
            var dteDebugger = _serviceProvider.GetDTE().Debugger;
            var frames = dteDebugger.CurrentThread.StackFrames;
            var currentFrame = dteDebugger.CurrentStackFrame;
            for (int i=0; i<frames.Count; i++) {
                if (frames.Item(i) == currentFrame && i < frames.Count - 1) {
                    dteDebugger.CurrentStackFrame = frames.Item(i + 1);
                    break;
                }
            }
        }

        internal void FrameDown() {
            var dteDebugger = _serviceProvider.GetDTE().Debugger;
            var frames = dteDebugger.CurrentThread.StackFrames;
            var currentFrame = dteDebugger.CurrentStackFrame;
            for (int i = 0; i < frames.Count; i++) {
                if (frames.Item(i) == currentFrame && i > 0) {
                    dteDebugger.CurrentStackFrame = frames.Item(i - 1);
                    break;
                }
            }
        }

        internal void StepOut() {
            UpdateDTEDebuggerProcessAndThread();
            _serviceProvider.GetDTE().Debugger.CurrentThread.Parent.StepOut();
        }

        internal void StepInto() {
            UpdateDTEDebuggerProcessAndThread();
            _serviceProvider.GetDTE().Debugger.CurrentThread.Parent.StepInto();
        }

        internal void StepOver() {
            UpdateDTEDebuggerProcessAndThread();
            _serviceProvider.GetDTE().Debugger.CurrentThread.Parent.StepOver();
        }

        internal void Resume() {
            UpdateDTEDebuggerProcessAndThread();
            _serviceProvider.GetDTE().Debugger.CurrentThread.Parent.Go();
        }

        private void UpdateDTEDebuggerProcessAndThread() {
            EnvDTE.Process dteActiveProcess = null;
            foreach (EnvDTE.Process dteProcess in _serviceProvider.GetDTE().Debugger.DebuggedProcesses) {
                if (dteProcess.ProcessID == _currentProcessId) {
                    dteActiveProcess = dteProcess;
                    break;
                }
            }

            if (dteActiveProcess != _serviceProvider.GetDTE().Debugger.CurrentProcess) {
                _serviceProvider.GetDTE().Debugger.CurrentProcess = dteActiveProcess;
            }

            EnvDTE.Thread dteActiveThread = null;
            foreach (EnvDTE.Thread dteThread in _serviceProvider.GetDTE().Debugger.CurrentProgram.Threads) {
                if ((uint)dteThread.ID == _threadId) {
                    dteActiveThread = dteThread;
                    break;
                }
            }

            if (dteActiveThread != _serviceProvider.GetDTE().Debugger.CurrentThread) {
                _serviceProvider.GetDTE().Debugger.CurrentThread = dteActiveThread;
            }
        }
    }
}
