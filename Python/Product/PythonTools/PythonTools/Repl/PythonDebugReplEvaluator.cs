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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Parsing;
using Microsoft.PythonTools.Debugger;
using Microsoft.PythonTools.Debugger.DebugEngine;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Options;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.InteractiveWindow.Commands;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudioTools;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.PythonTools.Repl {
    [InteractiveWindowRole("Debug")]
    [ContentType(PythonCoreConstants.ContentType)]
    [ContentType(PredefinedInteractiveCommandsContentTypes.InteractiveCommandContentTypeName)]
    internal class PythonDebugReplEvaluator :
        IInteractiveEvaluator,
        IPythonInteractiveEvaluator,
        IMultipleScopeEvaluator,
        IPythonInteractiveIntellisense {
        private PythonDebugProcessReplEvaluator _activeEvaluator;
        private readonly Dictionary<int, PythonDebugProcessReplEvaluator> _evaluators = new Dictionary<int, PythonDebugProcessReplEvaluator>(); // process id to evaluator
        private readonly Dictionary<int, Task> _attachingTasks = new Dictionary<int, Task>();
        private EnvDTE.DebuggerEvents _debuggerEvents;
        private readonly PythonToolsService _pyService;
        private readonly IServiceProvider _serviceProvider;
        private IInteractiveWindowCommands _commands;
        private Uri _documentUri;

        private static readonly string currentPrefix = Strings.DebugReplCurrentIndicator;
        private static readonly string notCurrentPrefix = Strings.DebugReplNotCurrentIndicator;

        public PythonDebugReplEvaluator(IServiceProvider serviceProvider) {
            _serviceProvider = serviceProvider;
            _pyService = serviceProvider.GetPythonToolsService();
            DebuggerHelper.Instance.ProcessExited += new EventHandler(OnProcessExited);
            var dte = _serviceProvider.GetDTE();
            if (dte != null) {
                // running outside of VS, make this work for tests.
                _debuggerEvents = dte.Events.DebuggerEvents;
                _debuggerEvents.OnEnterBreakMode += new EnvDTE._dispDebuggerEvents_OnEnterBreakModeEventHandler(OnEnterBreakMode);
            }
        }

        internal PythonInteractiveOptions CurrentOptions {
            get {
                return _pyService.DebugInteractiveOptions;
            }
        }

        private bool IsInDebugBreakMode() {
            var dte = _serviceProvider.GetDTE();
            if (dte == null) {
                // running outside of VS, make this work for tests.
                return true;
            }
            return dte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode;
        }

        private void OnReadyForInput() {
            OnReadyForInputAsync().HandleAllExceptions(_serviceProvider, GetType()).DoNotWait();
        }

        private async Task OnReadyForInputAsync() {
            if (IsInDebugBreakMode()) {
                var debugger = DebuggerHelper.Instance;
                if (debugger.CurrentProcessId != 0) {
                    if (!_evaluators.ContainsKey(debugger.CurrentProcessId)) {
                        await AttachProcessAsync(debugger.CurrentProcess, debugger.CurrentProcessId);
                    }
                }
            }
        }

        private void OnEnterBreakMode(EnvDTE.dbgEventReason Reason, ref EnvDTE.dbgExecutionAction ExecutionAction) {
            if (DebuggerHelper.Instance.CurrentProcess != null) {
                DebuggerHelper.Instance.CurrentProcess.EnumThreads(out var ppEnum);
                if (ppEnum != null) {
                    ppEnum.GetCount(out var count);
                    IDebugThread2[] pThreads = new IDebugThread2[count];
                    ppEnum.Next(count, pThreads, ref count);

                }
                if (_serviceProvider.GetDTE().Debugger.CurrentThread.ID != 0) {
                    AttachProcessAsync(DebuggerHelper.Instance.CurrentProcess, DebuggerHelper.Instance.CurrentProcessId).ContinueWith(t => {
                        ChangeActiveThread(DebuggerHelper.Instance.CurrentThread, false);
                    }).HandleAllExceptions(_serviceProvider, GetType()).DoNotWait();
                }
            }
        }

        public void ActiveLanguageBufferChanged(ITextBuffer currentBuffer, ITextBuffer previousBuffer) {
        }

        public bool CanExecuteCode(string text) {
            if (_commands.InCommand) {
                return true;
            }
            if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                return CanExecuteCodeExperimental(text);
            }
            if (_activeEvaluator != null) {
                return _activeEvaluator.CanExecuteCode(text);
            }
            return false;
        }

        bool CanExecuteCodeExperimental(string text) {
            var pr = ParseResult.Complete;
            if (string.IsNullOrEmpty(text)) {
                return true;
            }
            if (string.IsNullOrWhiteSpace(text) && text.EndsWithOrdinal("\n")) {
                //pr = ParseResult.Empty;
                return true;
            }

            var parser = Parser.CreateParser(new StringReader(text), PythonLanguageVersion.None);
            parser.ParseInteractiveCode(null, out pr);
            if (pr == ParseResult.IncompleteStatement || pr == ParseResult.Empty) {
                return text.EndsWithOrdinal("\n");
            }
            if (pr == ParseResult.IncompleteToken) {
                return false;
            }
            return true;
        }

        public Task<ExecutionResult> ExecuteCodeAsync(string text) {
            var res = _commands.TryExecuteCommand();
            if (res != null) {
                return res;
            }

            if (!IsInDebugBreakMode()) {
                NoExecutionIfNotStoppedInDebuggerError();
                return ExecutionResult.Succeeded;
            }

            if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                var tid = _serviceProvider.GetDTE().Debugger.CurrentThread.ID;
                (bool isSuccessful, string message) result = CustomDebugAdapterProtocolExtension.EvaluateReplRequest(text, tid);

                if (!result.isSuccessful) {
                    CurrentWindow.WriteError(result.message);
                    return ExecutionResult.Failed;
                }

                CurrentWindow.Write(result.message);
            } else if (_activeEvaluator != null) {
                return _activeEvaluator.ExecuteCodeAsync(text);
            } 

            return ExecutionResult.Succeeded;
        }

        public async Task<bool> ExecuteFileAsync(string filename, string extraArgs) {
            if (!IsInDebugBreakMode()) {
                NoExecutionIfNotStoppedInDebuggerError();
                return true;
            }

            if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
                return true;
            }

            var t = _activeEvaluator?.ExecuteFileAsync(filename, extraArgs);
            if (t != null) {
                return await t;
            }
            return true;
        }

        public void AbortExecution() {
            CurrentWindow.WriteErrorLine(Strings.DebugReplAbortNotSupported);
        }

        public Task<ExecutionResult> Reset() {
            CurrentWindow.WriteErrorLine(Strings.DebugReplResetNotSupported);
            return ExecutionResult.Succeeded;
        }

        public string FormatClipboard() {
            return PythonCommonInteractiveEvaluator.FormatClipboard(_serviceProvider, CurrentWindow);
        }

        public void Dispose() {
        }

        public IEnumerable<string> GetAvailableScopes() {
            string[] fixedScopes = new string[] { Strings.DebugReplCurrentFrameScope };
            if (_activeEvaluator != null) {
                return fixedScopes.Concat(_activeEvaluator.GetAvailableScopes());
            } else {
                return new string[0];
            }
        }

        public event EventHandler<EventArgs> AvailableScopesChanged;
        public event EventHandler<EventArgs> MultipleScopeSupportChanged;

        public void SetScope(string scopeName) {
            if (_activeEvaluator != null) {
                _activeEvaluator.SetScope(scopeName);
            } else {
            }
        }

        public string CurrentScopeName => _activeEvaluator?.CurrentScopeName ?? "";
        public string CurrentScopePath => _activeEvaluator?.CurrentScopePath ?? "";
        public bool EnableMultipleScopes => _activeEvaluator?.EnableMultipleScopes ?? false;

        public bool LiveCompletionsOnly {
            get { return CurrentOptions.LiveCompletionsOnly; }
        }

        public IInteractiveWindow CurrentWindow { get; set; }

        public Uri DocumentUri {
            get {
                if (_activeEvaluator != null) {
                    return _activeEvaluator.DocumentUri;
                } else if (_documentUri != null) {
                    return _documentUri;
                } else {
                    _documentUri = new Uri($"repl://{Guid.NewGuid()}/repl.py");
                    return _documentUri;
                }
            }
        }

        public Uri NextDocumentUri() => _activeEvaluator?.NextDocumentUri();

        public bool IsDisconnected => _activeEvaluator?.IsDisconnected ?? true;

        public bool IsExecuting => _activeEvaluator?.IsExecuting ?? false;

        public string DisplayName => Strings.DebugReplDisplayName;

        public PythonLanguageVersion LanguageVersion => _activeEvaluator?.LanguageVersion ?? PythonLanguageVersion.None;

        public IEnumerable<KeyValuePair<string, string>> GetAvailableScopesAndPaths() {
            if (_activeEvaluator != null) {
                return _activeEvaluator.GetAvailableScopesAndPaths();
            }

            return Enumerable.Empty<KeyValuePair<string, string>>();
        }

        public async Task<CompletionResult[]> GetMemberNamesAsync(string text, CancellationToken ct) {
            if (_activeEvaluator != null) {
                return await _activeEvaluator.GetMemberNamesAsync(text, ct);
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                var expression = string.Format(CultureInfo.InvariantCulture, "':'.join(dir({0}))", text ?? "");
                var tid = _serviceProvider.GetDTE().Debugger.CurrentThread.ID;
                (bool isSuccessful, string message) result = CustomDebugAdapterProtocolExtension.EvaluateReplRequest(text, tid);

                if (result.isSuccessful) {
                    var completionResults = result.message
                                    .Split(':')
                                    .Where(r => !string.IsNullOrEmpty(r))
                                    .Select(r => new CompletionResult(r, PythonMemberType.Generic))
                                    .ToArray();
                    return completionResults;
                }
            }

            return new CompletionResult[0];
        }

        public async Task<OverloadDoc[]> GetSignatureDocumentationAsync(string text, CancellationToken ct) {
            if (_activeEvaluator != null) {
                return await _activeEvaluator.GetSignatureDocumentationAsync(text, ct);
            }

            return new OverloadDoc[0];
        }

        internal void StepOut() {
            if (_activeEvaluator != null) {
                _activeEvaluator.StepOut();
                CurrentWindow.TextView.VisualElement.Focus();
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                _serviceProvider.GetDTE().Debugger.CurrentThread.Parent.StepOut();
                CurrentWindow.TextView.VisualElement.Focus();
            } else {
                NoProcessError();
            }
        }

        internal void StepInto() {
            if (_activeEvaluator != null) {
                _activeEvaluator.StepInto();
                CurrentWindow.TextView.VisualElement.Focus();
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                _serviceProvider.GetDTE().Debugger.CurrentThread.Parent.StepInto();
                CurrentWindow.TextView.VisualElement.Focus();
            } else {
                NoProcessError();
            }
        }

        internal void StepOver() {
            if (_activeEvaluator != null) {
                _activeEvaluator.StepOver();
                CurrentWindow.TextView.VisualElement.Focus();
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                _serviceProvider.GetDTE().Debugger.CurrentThread.Parent.StepOver();
                CurrentWindow.TextView.VisualElement.Focus();
            } else {
                NoProcessError();
            }
        }

        internal void Resume() {
            if (_activeEvaluator != null) {
                _activeEvaluator.Resume();
                CurrentWindow.TextView.VisualElement.Focus();
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                _serviceProvider.GetDTE().Debugger.CurrentThread.Parent.Go();
                CurrentWindow.TextView.VisualElement.Focus();
            } else {
                NoProcessError();
            }
        }

        internal void FrameUp() {
            if (_activeEvaluator != null) {
                _activeEvaluator.FrameUp();
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                NoProcessError();
            }
        }

        internal void FrameDown() {
            if (_activeEvaluator != null) {
                _activeEvaluator.FrameDown();
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                NoProcessError();
            }
        }

        internal void DisplayActiveProcess() {
            if (_activeEvaluator != null) {
                _activeEvaluator.WriteOutput(_activeEvaluator.ProcessId.ToString());
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                CurrentWindow.WriteLine("None" + Environment.NewLine);
            }
        }

        internal void DisplayActiveThread() {
            if (_activeEvaluator != null) {
                _activeEvaluator.WriteOutput(_activeEvaluator.ThreadId.ToString());
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                NoProcessError();
            }
        }

        internal void DisplayActiveFrame() {
        }

        internal void ChangeActiveProcess(int id, bool verbose) {
            if (_evaluators.Keys.Contains(id)) {
                //SwitchProcess(_evaluators[id].Process, verbose);
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                CurrentWindow.WriteErrorLine(Strings.DebugReplInvalidProcessId.FormatUI(id));
            }
        }

        internal void ChangeActiveThread(long threadId, bool verbose) {
            if (_activeEvaluator != null) {
                if (threadId != 0) {
                    _activeEvaluator.SwitchThread(threadId, verbose);
                } else {
                    CurrentWindow.WriteErrorLine(Strings.DebugReplInvalidThreadId.FormatUI(threadId));
                }
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                NoProcessError();
            }
        }

        internal void ChangeActiveThread(IDebugThread2 thread, bool verbose) {
            if (_activeEvaluator != null) {
                if (thread != null) {
                    _activeEvaluator.SwitchThread(thread, verbose);
                } else {
                    CurrentWindow.WriteErrorLine(Strings.DebugReplInvalidThreadId.FormatUI(0));
                }
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                NoProcessError();
            }
        }

        internal void ChangeActiveFrame(int id) {
            if (_activeEvaluator != null) {
                //PythonStackFrame frame = _activeEvaluator.GetFrames().SingleOrDefault(target => target.FrameId == id);
                //if (frame != null) {
                //    _activeEvaluator.SwitchFrame(frame);
                //} else {
                //    CurrentWindow.WriteErrorLine(Strings.DebugReplInvalidFrameId.FormatUI(id));
                //}
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                NoProcessError();
            }
        }

        internal void DisplayProcesses() {
            if (_activeEvaluator != null) {
                //foreach (var target in _evaluators.Values) {
                //    if (target.Process != null) {
                //        _activeEvaluator.WriteOutput(Strings.DebugReplProcessesOutput.FormatUI(target.Process.Id, target.Process.LanguageVersion, target.Process.Id == _activeEvaluator.ProcessId ? currentPrefix : notCurrentPrefix));
                //    }
                //}
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            }
        }

        internal void DisplayThreads() {
            if (_activeEvaluator != null) {
                //foreach (var target in _activeEvaluator.GetThreads()) {
                //    _activeEvaluator.WriteOutput(Strings.DebugReplThreadsOutput.FormatUI(target.Id, target.Name, target.Id == _activeEvaluator.ThreadId ? currentPrefix : notCurrentPrefix));
                //}
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                NoProcessError();
            }
        }

        internal void DisplayFrames() {
            if (_activeEvaluator != null) {
                //foreach (var target in _activeEvaluator.GetFrames()) {
                //    _activeEvaluator.WriteOutput(Strings.DebugReplFramesOutput.FormatUI(target.FrameId, target.FunctionName, target.FrameId == _activeEvaluator.FrameId ? currentPrefix : notCurrentPrefix));
                //}
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            } else {
                NoProcessError();
            }
        }

        private void OnEngineAttached(object sender, AD7EngineEventArgs e) {
            //_serviceProvider.GetUIThread().InvokeAsync(async () => {
            //    await AttachProcessAsync(e.Engine.Process, e.Engine);
            //}).HandleAllExceptions(_serviceProvider, GetType()).DoNotWait();
        }

        private void OnEngineDetaching(object sender, AD7EngineEventArgs e) {
            //_serviceProvider.GetUIThread().InvokeAsync(async () => {
            //    await DetachProcessAsync(e.Engine.Process);
            //}).HandleAllExceptions(_serviceProvider, GetType()).DoNotWait();
        }

        private void OnProcessExited(object sender, EventArgs e) {
            _serviceProvider.GetUIThread().InvokeAsync(async () => {
                await DetachProcessAsync(DebuggerHelper.Instance.CurrentProcessId);
            }).HandleAllExceptions(_serviceProvider, GetType()).DoNotWait();
        }

        internal void SwitchProcess(IDebugProcess2 process, int pid, bool verbose) {
            var newEvaluator = _evaluators[pid];
            if (newEvaluator != _activeEvaluator) {
                _activeEvaluator = newEvaluator;
                ActiveProcessChanged();
                if (verbose) {
                    CurrentWindow.WriteLine(Strings.DebugReplSwitchProcessOutput.FormatUI(pid));
                }
            } else if (CustomDebugAdapterProtocolExtension.CanUseExperimental()) {
                NotSupported();
            }
        }

        internal async Task AttachProcessAsync(IDebugProcess2 process, int pid) {
            // The fact that this is only called from UI thread is no guarantee
            // that there won't be more than one "instance" of this method in
            // progress at any time (though only one is executing while others are paused).

            // It's possible because this is an async method with await(s), and
            // UI thread execution will temporarily continue somewhere else when
            // await is used, and that somewhere else can be code that calls
            // into this method with the same process id!

            // The more relevant trigger for this cooperative multitasking is
            // the await on evaluator.InitializeAsync, and when that happens
            // the evaluator is not in the _evaluators dictionary yet.

            // If a second caller comes in (on the same UI thread) during that
            // await, it gets past the first check because it's not in _evaluators,
            // and then checks the _attachingTasks dictionary and know to wait
            // for that instead of creating a new evaluator.

            // Note that adding the uninitialized evaluator to the _evaluators
            // dictionary would be a potentially bug prone solution, as other
            // code may try to use it before it's fully initialized.
            _serviceProvider.GetUIThread().MustBeCalledFromUIThread();

            if (_evaluators.ContainsKey(pid)) {
                // Process is already attached, so just switch to it if needed
                SwitchProcess(process, pid, false);
                return;
            }

            // Keep track of evaluators that are in progress of attaching, and if
            // we are getting called to attach for one that is already in progress,
            // just wait for it to finish before returning.
            // Important: dictionary must be checked (and updated) before any
            // await call to avoid race condition.
            Task attachingTask;
            TaskCompletionSource<object> attachingTcs = null;
            if (_attachingTasks.TryGetValue(pid, out attachingTask)) {
                await attachingTask;
                return;
            } else {
                attachingTcs = new TaskCompletionSource<object>();
                _attachingTasks.Add(pid, attachingTcs.Task);
            }

            var evaluator = new PythonDebugProcessReplEvaluator(_serviceProvider, process, pid) {
                CurrentWindow = CurrentWindow
            };
            evaluator.AvailableScopesChanged += new EventHandler<EventArgs>(evaluator_AvailableScopesChanged);
            evaluator.MultipleScopeSupportChanged += new EventHandler<EventArgs>(evaluator_MultipleScopeSupportChanged);
            await evaluator.InitializeAsync();
            _evaluators.Add(pid, evaluator);

            _activeEvaluator = evaluator;

            // Only refresh available scopes after the active evaluator has
            // been changed, because that's where the UI will look.
            await evaluator.RefreshAvailableScopes();

            attachingTcs.SetResult(null);
            _attachingTasks.Remove(pid);
        }

        internal async Task DetachProcessAsync(int pid) {
            _serviceProvider.GetUIThread().MustBeCalledFromUIThread();

            PythonDebugProcessReplEvaluator evaluator;
            if (_evaluators.TryGetValue(pid, out evaluator)) {
                evaluator.AvailableScopesChanged -= new EventHandler<EventArgs>(evaluator_AvailableScopesChanged);
                evaluator.MultipleScopeSupportChanged -= new EventHandler<EventArgs>(evaluator_MultipleScopeSupportChanged);
                _evaluators.Remove(pid);
                if (_activeEvaluator == evaluator) {
                    _activeEvaluator = null;
                }

                ActiveProcessChanged();
            }
        }

        private void evaluator_MultipleScopeSupportChanged(object sender, EventArgs e) {
            MultipleScopeSupportChanged?.Invoke(this, EventArgs.Empty);
        }

        private void evaluator_AvailableScopesChanged(object sender, EventArgs e) {
            AvailableScopesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ActiveProcessChanged() {
            MultipleScopeSupportChanged?.Invoke(this, EventArgs.Empty);
            AvailableScopesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void NoProcessError() {
            CurrentWindow.WriteErrorLine(Strings.DebugReplNoProcessError);
        }

        private void NotSupported() {
            CurrentWindow.WriteError(Strings.DebugReplFeatureNotSupported);
        }

        private void NoExecutionIfNotStoppedInDebuggerError() {
            CurrentWindow.WriteErrorLine(Strings.DebugReplNoExecutionIfNotStoppedInDebuggerError);
        }

        public Task<ExecutionResult> InitializeAsync() {
            _commands = PythonCommonInteractiveEvaluator.GetInteractiveCommands(_serviceProvider, CurrentWindow, this);

            CurrentWindow.TextView.Options.SetOptionValue(InteractiveWindowOptions.SmartUpDown, CurrentOptions.UseSmartHistory);
            CurrentWindow.WriteLine(Strings.DebugReplHelpMessage);

            CurrentWindow.ReadyForInput += OnReadyForInput;
            return ExecutionResult.Succeeded;
        }

        public Task<ExecutionResult> ResetAsync(bool initialize = true) {
            return Reset();
        }

        public string GetPrompt() {
            return _activeEvaluator?.GetPrompt();
        }

        public Task<object> GetAnalysisCompletions(LSP.Position triggerPoint, LSP.CompletionContext context, CancellationToken token) {
            // No analysis completions for debug repl
            return Task.FromResult<object>(null);
        }
    }

    internal static class PythonDebugReplEvaluatorExtensions {
        public static PythonDebugReplEvaluator GetPythonDebugReplEvaluator(this IInteractiveWindow window) {
            var eval = window?.Evaluator as PythonDebugReplEvaluator;
            if (eval != null) {
                return eval;
            }

            eval = (window?.Evaluator as SelectableReplEvaluator)?.Evaluator as PythonDebugReplEvaluator;
            return eval;
        }
    }
}
