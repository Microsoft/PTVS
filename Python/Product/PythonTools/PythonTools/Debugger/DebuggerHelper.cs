using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Debugger.DebugEngine;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.PythonTools.Debugger {
    /// <summary>
    /// Helper class to expose IDebug* variables for use elsewhere (instead of using EnvDTE.Debugger)
    /// </summary>
    public class DebuggerHelper: IDebugEventCallback2 {
        private static DebuggerHelper _instance = new DebuggerHelper();
        private IDebugEngine2 _currentEngine;
        private IDebugProcess2 _currentProcess;
        private IDebugProgram2 _currentProgram;
        private IDebugThread2 _currentThread;
        private int _currentProcessId;

        private DebuggerHelper() {
            var debugger = Package.GetGlobalService(typeof(SVsShellDebugger)) as IVsDebugger;
            debugger.AdviseDebugEventCallback(this);
        }

        public event EventHandler ProcessExited;
        public event EventHandler ModulesChanged;

        public int Event(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent, ref Guid riidEvent, uint dwAttrib) {
            if (riidEvent == typeof(IDebugModuleLoadEvent2).GUID) {
                if (_currentEngine != null) {
                    ModulesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            
            if (riidEvent == typeof(IDebugSessionDestroyEvent2).GUID) {
                if (_currentEngine != null) {
                    ProcessExited?.Invoke(this, EventArgs.Empty);
                }
                ComDisposer.DisposeComObjects(_currentProgram, _currentProcess, _currentEngine);
                _currentEngine = null;
                _currentProcess = null;
                _currentProgram = null;
                _currentProcessId = 0;
            } else if (_currentProcess == null && IsPythonDebugger(pEngine)) {
                _currentProgram = pProgram;
                _currentProcess = pProcess;
                _currentEngine = pEngine;
                AD_PROCESS_ID[] processId = new AD_PROCESS_ID[0];
                CurrentProcess.GetPhysicalProcessId(processId);
                _currentProcessId = (int)processId[0].dwProcessId;
            }

            // Thread changes as events happen
            if (_currentThread != pThread) {
                ComDisposer.DisposeComObjects(_currentThread);
                _currentThread = pThread;
            }

            return VSConstants.S_OK;
        }

        public static DebuggerHelper Instance => _instance;

        public IDebugEngine2 CurrentEngine => _currentEngine;

        public IDebugProcess2 CurrentProcess => _currentProcess;

        public IDebugProgram2 CurrentProgram => _currentProgram;

        public int CurrentProcessId => _currentProcessId;

        public IDebugThread2 CurrentThread => _currentThread;

        public KeyValuePair<string, string>[] GetModuleNamesAndPaths() {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            if (_currentProgram != null) {
                _currentProgram.EnumModules(out var ppEnum);
                using (var disposer = new ComDisposer(ppEnum)) { 
                    ppEnum.GetCount(out var count);
                    IDebugModule2[] pModules = new IDebugModule2[count];
                    ppEnum.Next(count, pModules, ref count);
                    foreach (var module in pModules) {
                        disposer.Add(module);
                        MODULE_INFO[] pModuleInfo = new MODULE_INFO[1];
                        module.GetInfo(enum_MODULE_INFO_FIELDS.MIF_ALLFIELDS, pModuleInfo);
                        result.Add(new KeyValuePair<string, string>(pModuleInfo[0].m_bstrName, pModuleInfo[0].m_bstrUrl));
                    }
                }
            }
            return result.ToArray();
        }

        public static IDebugStackFrame2 GetTopmostFrame(IDebugThread2 thread) {
            if (thread != null) {
                using (var disposer = new ComDisposer()) {
                    thread.EnumFrameInfo(enum_FRAMEINFO_FLAGS.FIF_FRAME, 0, out var frameEnum);
                    disposer.Add(frameEnum);
                    frameEnum.GetCount(out var count);
                    FRAMEINFO[] frameInfo = new FRAMEINFO[1];
                    frameEnum.Next(1, frameInfo, ref count);
                    return frameInfo[0].m_pFrame;
                }
            }
            return null;
        }

        public Task<Tuple<ExecutionResult, string>> EvaluateText(string text, uint timeout) {
            if (_currentThread != null) {
                using (var disposer = new ComDisposer()) {
                    var frame = GetTopmostFrame(_currentThread);
                    disposer.Add(frame);
                    frame.GetExpressionContext(out var expressionContext);
                    disposer.Add(expressionContext);
                    expressionContext.ParseText(text, enum_PARSEFLAGS.PARSE_EXPRESSION, 10, out var ppExpr, out var pbstrError, out var pichError);
                    if (String.IsNullOrEmpty(pbstrError)) {
                        return Task.FromResult(Tuple.Create(ExecutionResult.Failure, pbstrError));
                    } else {
                        var completionHandler = new EvaluateCompletionHandler();
                        var expressionAsync = (IDebugExpression157)ppExpr; // Should always work in 15.7 and higher

                        var infoFlags = enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_ALL;
                        var hr = expressionAsync.GetEvaluateAsyncOp((uint)infoFlags, 10, (uint)enum_EVALFLAGS110.EVAL110_FORCE_REAL_FUNCEVAL, timeout, completionHandler, out var operation);
                        if (hr != VSConstants.S_OK) {
                            throw Marshal.GetExceptionForHR(hr, (IntPtr)(-1));
                        }

                        hr = operation.BeginExecute();
                        if (hr != VSConstants.S_OK) {
                            throw Marshal.GetExceptionForHR(hr, (IntPtr)(-1));
                        }

                        return completionHandler.Task;
                    }
                }
            }

            return Task.FromResult(Tuple.Create(ExecutionResult.Failure, "No thread to evaluate on"));
        }

        private static bool IsPythonDebugger(IDebugEngine2 engine) {
            if (engine != null) {
                engine.GetEngineId(out var guid);
                if (guid == AD7Engine.DebugEngineGuid) {
                    return true;
                }
            }
            return false;
        }

        public static bool IsRemote(IDebugProcess2 process) {
            process.GetPort(out var port);
            using (var disposer = new ComDisposer(port)) {
                return IsRemotePort(port);
            }
        }

        public static bool IsRemotePort(IDebugPort2 port) {
            try {
                var iface = Marshal.GetComInterfaceForObject(port, typeof(IDebugUnixShellPort));
                var result = iface != IntPtr.Zero;
                Marshal.Release(iface);
                return result;
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Completion handler used by EvaluateAsync
        /// </summary>
        private class EvaluateCompletionHandler : IAsyncDebugEvaluateCompletionHandler {
            private readonly TaskCompletionSource<Tuple<ExecutionResult, string>> tcs = new TaskCompletionSource<Tuple<ExecutionResult, string>>();

            public CancellationTokenRegistration CancellationTokenRegistration { get; private set; }
            public Task<Tuple<ExecutionResult, string>> Task => this.tcs.Task;

            internal void RegisterForCancellation(CancellationToken cancellationToken, IAsyncDebugEngineOperation operation) {
                CancellationTokenRegistration = cancellationToken.Register(() => operation.Cancel());
            }

            public int OnComplete(int hr, IDebugProperty2 pDebugProperty) {
                // This call will always happen on the VS UI thread
                CancellationTokenRegistration.Dispose();
                using (var disposer = new ComDisposer(pDebugProperty)) {
                    if (hr >= VSConstants.S_OK) {
                        var propInfo = new DEBUG_PROPERTY_INFO[1];
                        var regArgs = new IDebugReference2[0];
                        pDebugProperty.GetPropertyInfo(enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_VALUE, 10, 1000, regArgs, 0, propInfo);
                        this.tcs.SetResult(Tuple.Create(ExecutionResult.Success, propInfo[0].bstrValue));
                    } else {
                        this.tcs.SetException(Marshal.GetExceptionForHR(hr, (IntPtr)(-1)));
                    }

                }


                return VSConstants.S_OK;
            }
        };

    }
}
