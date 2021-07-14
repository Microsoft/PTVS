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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using Microsoft.Python.Parsing;
using Microsoft.PythonTools.Debugger.Remote;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.PythonTools.Debugger.DebugEngine {
    // AD7Engine is the primary entrypoint object for the debugging engine. 
    //
    // It implements:
    //
    // IDebugEngine2: This interface represents a debug engine (DE). It is used to manage various aspects of a debugging session, 
    // from creating breakpoints to setting and clearing exceptions.
    //
    // IDebugEngineLaunch2: Used by a debug engine (DE) to launch and terminate programs.
    //
    // IDebugProgram3: This interface represents a program that is running in a process. Since this engine only debugs one process at a time and each 
    // process only contains one program, it is implemented on the engine.

    [SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
        Justification = "We do not control ownership of this class")]
    [ComVisible(true)]
    [Guid(Guids.DebugEngineCLSID)]
    public sealed class AD7Engine : IDebugEngine2, IDebugEngineLaunch2, IDebugProgram3, IDebugSymbolSettings100 {
        // used to send events to the debugger. Some examples of these events are thread create, exception thrown, module load.
        private IDebugEventCallback2 _events;

        private IDebugEngine3 _adapterHostEngine = null; // VSCodeDebugAdapterHost engine 
        private IDebugEngineLaunch2 _adapterHostEngineLaunch = null;
        private IDebugProgram3 _adapterHostProgram = null;
        private static HashSet<WeakReference> _engines = new HashSet<WeakReference>();
        private static readonly Guid AdapterHostClsid = new Guid("DAB324E9-7B35-454C-ACA8-F6BB0D5C8673");
        private static readonly Guid AdapterHostEngineId = new Guid("{86432F39-ADFD-4C56-AA8F-AF8FCDC66039}");

        internal static event EventHandler<AD7EngineEventArgs> EngineAttached;
        internal static event EventHandler<AD7EngineEventArgs> EngineDetaching;

        // These constants are duplicated in HpcLauncher and cannot be changed

        public const string DebugEngineId = "{EC1375B7-E2CE-43E8-BF75-DC638DE1F1F9}";
        public const string DebugEngineName = "Python";
        public static Guid DebugEngineGuid = new Guid(DebugEngineId);
        public const string SourceDirectoryKey = "sd";
        public const string TargetDirectoryKey = "td";
        public const string TargetHostType = "host";
        public const string DebugOptionsKey = "opt";
        public const string DebugOptionsMetric = "PythonDebugOptions";

        public const string TargetUwp = "uwp";

        /// <summary>
        /// Specifies the version of the language which is being debugged.  One of
        /// V24, V25, V26, V27, V30, V31 or V32.
        /// </summary>
        public const string VersionSetting = "VERSION";

        /// <summary>
        /// Specifies whether the process should prompt for input before exiting on an abnormal exit.
        /// </summary>
        public const string WaitOnAbnormalExitSetting = "WAIT_ON_ABNORMAL_EXIT";

        /// <summary>
        /// Specifies whether the process should prompt for input before exiting on a normal exit.
        /// </summary>
        public const string WaitOnNormalExitSetting = "WAIT_ON_NORMAL_EXIT";

        /// <summary>
        /// Specifies if the output should be redirected to the visual studio output window.
        /// </summary>
        public const string RedirectOutputSetting = "REDIRECT_OUTPUT";

        /// <summary>
        /// Specifies if the debugger should break on SystemExit exceptions with an exit code of zero.
        /// </summary>
        public const string BreakSystemExitZero = "BREAK_SYSTEMEXIT_ZERO";

        /// <summary>
        /// Specifies if the debugger should step/break into std lib code.
        /// </summary>
        public const string DebugStdLib = "DEBUG_STDLIB";

        /// <summary>
        /// Specifies the debugger should stop on first statement.
        /// </summary>
        public const string StopOnEntry = "STOP_ON_ENTRY";

        /// <summary>
        /// Specifies if the debugger should display the function return values in locals window
        /// </summary>
        public const string ShowReturnValue = "SHOW_RETURN_VALUE";

        /// <summary>
        /// Specifies if the debugger should treat the application as if it doesn't have a console.
        /// </summary>
        /// <remarks>
        /// Currently, the only effect this has is suppressing <see cref="WaitOnAbnormalExitSetting"/>
        /// and <see cref="WaitOnNormalExitSetting"/> if they're set.
        /// </remarks>
        public const string IsWindowsApplication = "IS_WINDOWS_APPLICATION";

        /// <summary>
        /// Specifies options which should be passed to the Python interpreter before the script.  If
        /// the interpreter options should include a semicolon then it should be escaped as a double
        /// semi-colon.
        /// </summary>
        public const string InterpreterOptions = "INTERPRETER_OPTIONS";

        public const string AttachRunning = "ATTACH_RUNNING";

        /// <summary>
        /// Specifies URL to which to open web browser on debug connect.
        /// </summary>
        public const string WebBrowserUrl = "WEB_BROWSER_URL";

        /// <summary>
        /// True if Django debugging is enabled.
        /// </summary>
        public const string EnableDjangoDebugging = "DJANGO_DEBUG";

        /// <summary>
        /// Specifies a directory mapping in the form of:
        /// 
        /// OldDir|NewDir
        /// 
        /// for mapping between the files on the local machine and the files deployed on the
        /// running machine.
        /// </summary>
        public const string DirMappingSetting = "DIR_MAPPING";
        public AD7Engine() {
            Debug.WriteLine("Python Engine Created " + GetHashCode());
            var localRegistry = ServiceProvider.GlobalProvider.GetService(typeof(SLocalRegistry)) as ILocalRegistry;
            var adapterHostEngine = localRegistry.CreateInstance(AdapterHostClsid);
            _adapterHostEngine = adapterHostEngine as IDebugEngine3;
            _adapterHostEngineLaunch = _adapterHostEngine as IDebugEngineLaunch2;
            _adapterHostEngine.SetEngineGuid(AdapterHostEngineId);
        }

        ~AD7Engine() {
            Debug.WriteLine("Python Engine Finalized " + GetHashCode());
            if (_adapterHostEngine is IDisposable disposable) {
                disposable.Dispose();
            }
            _adapterHostEngine = null;
        }


        #region IDebugEngine2 Members

        // Attach the debug engine to a program. 
        int IDebugEngine2.Attach(IDebugProgram2[] rgpPrograms, IDebugProgramNode2[] rgpProgramNodes, uint celtPrograms, IDebugEventCallback2 ad7Callback, enum_ATTACH_REASON dwReason) {
            _events = ad7Callback;
            return _adapterHostEngine.Attach(rgpPrograms, rgpProgramNodes, celtPrograms, ad7Callback, dwReason);
        }

        // Requests that all programs being debugged by this DE stop execution the next time one of their threads attempts to run.
        // This is normally called in response to the user clicking on the pause button in the debugger.
        // When the break is complete, an AsyncBreakComplete event will be sent back to the debugger.
        int IDebugEngine2.CauseBreak() {
            return _adapterHostEngine.CauseBreak();
        }

        // Called by the SDM to indicate that a synchronous debug event, previously sent by the DE to the SDM,
        // was received and processed. 
        int IDebugEngine2.ContinueFromSynchronousEvent(IDebugEvent2 eventObject) {
            return _adapterHostEngine.ContinueFromSynchronousEvent(eventObject);
        }

        // Creates a pending breakpoint in the engine. A pending breakpoint is contains all the information needed to bind a breakpoint to 
        // a location in the debuggee.
        int IDebugEngine2.CreatePendingBreakpoint(IDebugBreakpointRequest2 pBPRequest, out IDebugPendingBreakpoint2 ppPendingBP) {
            return _adapterHostEngine.CreatePendingBreakpoint(pBPRequest, out ppPendingBP);
        }

        // Informs a DE that the program specified has been atypically terminated and that the DE should 
        // clean up all references to the program and send a program destroy event.
        int IDebugEngine2.DestroyProgram(IDebugProgram2 pProgram) {
            return _adapterHostEngine.DestroyProgram(pProgram);
        }

        // Gets the GUID of the DE.
        int IDebugEngine2.GetEngineId(out Guid guidEngine) {
            guidEngine = new Guid(DebugEngineId);
            return VSConstants.S_OK;
        }

        int IDebugEngine2.RemoveAllSetExceptions(ref Guid guidType) {
            return _adapterHostEngine.RemoveAllSetExceptions(ref guidType);
        }

        int IDebugEngine2.RemoveSetException(EXCEPTION_INFO[] pException) {
            return _adapterHostEngine.RemoveSetException(pException);
        }

        int IDebugEngine2.SetException(EXCEPTION_INFO[] pException) {
            return _adapterHostEngine.SetException(pException);
        }

        // Sets the locale of the DE.
        // This method is called by the session debug manager (SDM) to propagate the locale settings of the IDE so that
        // strings returned by the DE are properly localized. The engine is not localized so this is not implemented.
        int IDebugEngine2.SetLocale(ushort wLangID) {
            return VSConstants.S_OK;
        }

        // A metric is a registry value used to change a debug engine's behavior or to advertise supported functionality. 
        // This method can forward the call to the appropriate form of the Debugging SDK Helpers function, SetMetric.
        int IDebugEngine2.SetMetric(string pszMetric, object varValue) {
            return _adapterHostEngine.SetMetric(pszMetric, varValue);
        }

        // Sets the registry root currently in use by the DE. Different installations of Visual Studio can change where their registry information is stored
        // This allows the debugger to tell the engine where that location is.
        int IDebugEngine2.SetRegistryRoot(string pszRegistryRoot) {
            return _adapterHostEngine.SetRegistryRoot(pszRegistryRoot);
        }

        #endregion

        #region IDebugEngineLaunch2 Members

        // Determines if a process can be terminated.
        int IDebugEngineLaunch2.CanTerminateProcess(IDebugProcess2 process) {
            if (_adapterHostEngineLaunch == null) {
                return VSConstants.S_OK;
            }
            return _adapterHostEngineLaunch.CanTerminateProcess(process);
        }

        // Launches a process by means of the debug engine.
        // Normally, Visual Studio launches a program using the IDebugPortEx2::LaunchSuspended method and then attaches the debugger 
        // to the suspended program. However, there are circumstances in which the debug engine may need to launch a program 
        // (for example, if the debug engine is part of an interpreter and the program being debugged is an interpreted language), 
        // in which case Visual Studio uses the IDebugEngineLaunch2::LaunchSuspended method
        // The IDebugEngineLaunch2::ResumeProcess method is called to start the process after the process has been successfully launched in a suspended state.
        int IDebugEngineLaunch2.LaunchSuspended(string pszServer, IDebugPort2 port, string exe, string args, string dir, string env, string options, enum_LAUNCH_FLAGS launchFlags, uint hStdInput, uint hStdOutput, uint hStdError, IDebugEventCallback2 ad7Callback, out IDebugProcess2 process) {
            process = null;
            if (_adapterHostEngineLaunch == null) {
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostEngineLaunch.LaunchSuspended(pszServer, port, exe, args, dir, env, options, launchFlags, hStdInput, hStdOutput, hStdError, ad7Callback, out process);
        }

        private static string[] SplitOptions(string options) {
            List<string> res = new List<string>();
            int lastStart = 0;
            for (int i = 0; i < options.Length; i++) {
                if (options[i] == ';') {
                    if (i < options.Length - 1 && options[i + 1] != ';') {
                        // valid option boundary
                        res.Add(options.Substring(lastStart, i - lastStart));
                        lastStart = i + 1;
                    } else {
                        i++;
                    }
                }
            }
            if (options.Length - lastStart > 0) {
                res.Add(options.Substring(lastStart, options.Length - lastStart));
            }
            return res.ToArray();
        }

        // Resume a process launched by IDebugEngineLaunch2.LaunchSuspended
        int IDebugEngineLaunch2.ResumeProcess(IDebugProcess2 process) {
            EngineAttached?.Invoke(this, new AD7EngineEventArgs(this));

            if (_adapterHostEngineLaunch == null) {
                return VSConstants.E_NOTIMPL;
            }

            return _adapterHostEngineLaunch.ResumeProcess(process);
        }

        // This function is used to terminate a process that the engine launched
        // The debugger will call IDebugEngineLaunch2::CanTerminateProcess before calling this method.
        int IDebugEngineLaunch2.TerminateProcess(IDebugProcess2 process) {
            if (_adapterHostEngineLaunch == null) {
                return VSConstants.E_NOTIMPL;
            }
            EngineDetaching?.Invoke(this, new AD7EngineEventArgs(this));
            return _adapterHostEngineLaunch.TerminateProcess(process);
        }

        #endregion

        #region IDebugProgram2 Members

        // Determines if a debug engine (DE) can detach from the program.
        public int CanDetach() {
            if (_adapterHostProgram == null) {
                return VSConstants.S_FALSE;
            }
            return _adapterHostProgram.CanDetach();
        }

        // The debugger calls CauseBreak when the user clicks on the pause button in VS. The debugger should respond by entering
        // breakmode. 
        public int CauseBreak() {
            if (_adapterHostProgram == null) {
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostProgram.CauseBreak();
        }

        // Continue is called from the SDM when it wants execution to continue in the debugee
        // but have stepping state remain. An example is when a tracepoint is executed, 
        // and the debugger does not want to actually enter break mode.
        // It is also called to continue after load complete and autoresume after
        // entry point hit (when starting debugging with F5).
        public int Continue(IDebugThread2 pThread) {
            if (_adapterHostProgram == null) {
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostProgram.Continue(pThread);
        }

        // Detach is called when debugging is stopped and the process was attached to (as opposed to launched)
        // or when one of the Detach commands are executed in the UI.
        public int Detach() {
            if (_adapterHostProgram == null) {
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostProgram.Detach();
        }

        // Enumerates the code contexts for a given position in a source file.
        public int EnumCodeContexts(IDebugDocumentPosition2 pDocPos, out IEnumDebugCodeContexts2 ppEnum) {
            if (_adapterHostProgram == null) {
                ppEnum = null;
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostProgram.EnumCodeContexts(pDocPos, out ppEnum);
        }

        // EnumCodePaths is used for the step-into specific feature -- right click on the current statment and decide which
        // function to step into. This is not something that we support.
        public int EnumCodePaths(string hint, IDebugCodeContext2 start, IDebugStackFrame2 frame, int fSource, out IEnumCodePaths2 pathEnum, out IDebugCodeContext2 safetyContext) {
            pathEnum = null;
            safetyContext = null;
            return VSConstants.E_NOTIMPL;
        }

        // EnumModules is called by the debugger when it needs to enumerate the modules in the program.
        public int EnumModules(out IEnumDebugModules2 ppEnum) {
            if (_adapterHostProgram == null) {
                ppEnum = null;
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostProgram.EnumModules(out ppEnum);
        }

        // EnumThreads is called by the debugger when it needs to enumerate the threads in the program.
        public int EnumThreads(out IEnumDebugThreads2 ppEnum) {
            if (_adapterHostProgram == null) {
                ppEnum = null;
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostProgram.EnumThreads(out ppEnum);
        }

        // The properties returned by this method are specific to the program. If the program needs to return more than one property, 
        // then the IDebugProperty2 object returned by this method is a container of additional properties and calling the 
        // IDebugProperty2::EnumChildren method returns a list of all properties.
        // A program may expose any number and type of additional properties that can be described through the IDebugProperty2 interface. 
        // An IDE might display the additional program properties through a generic property browser user interface.
        public int GetDebugProperty(out IDebugProperty2 ppProperty) {
            throw new Exception("The method or operation is not implemented.");
        }

        // The debugger calls this when it needs to obtain the IDebugDisassemblyStream2 for a particular code-context.
        public int GetDisassemblyStream(enum_DISASSEMBLY_STREAM_SCOPE dwScope, IDebugCodeContext2 codeContext, out IDebugDisassemblyStream2 disassemblyStream) {
            disassemblyStream = null;
            return VSConstants.E_NOTIMPL;
        }

        // This method gets the Edit and Continue (ENC) update for this program. A custom debug engine always returns E_NOTIMPL
        public int GetENCUpdate(out object update) {
            update = null;
            return VSConstants.S_OK;
        }

        // Gets the name and identifier of the debug engine (DE) running this program.
        public int GetEngineInfo(out string engineName, out Guid engineGuid) {
            engineName = "Python";
            engineGuid = new Guid(DebugEngineId);
            return VSConstants.S_OK;
        }

        // The memory bytes as represented by the IDebugMemoryBytes2 object is for the program's image in memory and not any memory 
        // that was allocated when the program was executed.
        public int GetMemoryBytes(out IDebugMemoryBytes2 ppMemoryBytes) {
            throw new Exception("The method or operation is not implemented.");
        }

        // Gets the name of the program.
        // The name returned by this method is always a friendly, user-displayable name that describes the program.
        public int GetName(out string programName) {
            // The engine uses default transport and doesn't need to customize the name of the program,
            // so return NULL.
            programName = null;
            return VSConstants.S_OK;
        }

        // Gets a GUID for this program. A debug engine (DE) must return the program identifier originally passed to the IDebugProgramNodeAttach2::OnAttach
        // or IDebugEngine2::Attach methods. This allows identification of the program across debugger components.
        public int GetProgramId(out Guid guidProgramId) {
            if (_adapterHostProgram == null) {
                guidProgramId = Guid.Empty;
                return VSConstants.E_FAIL;
            }
            return _adapterHostProgram.GetProgramId(out guidProgramId);
        }

        // This method is deprecated. Use the IDebugProcess3::Step method instead.

        /// <summary>
        /// Performs a step. 
        /// 
        /// In case there is any thread synchronization or communication between threads, other threads in the program should run when a particular thread is stepping.
        /// </summary>
        public int Step(IDebugThread2 pThread, enum_STEPKIND sk, enum_STEPUNIT Step) {
            if (_adapterHostProgram == null) {
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostProgram.Step(pThread, sk, Step);
        }

        // Terminates the program.
        public int Terminate() {
            Debug.WriteLine("PythonEngine Terminate");
            // Because we implement IDebugEngineLaunch2 we will terminate
            // the process in IDebugEngineLaunch2.TerminateProcess
            return VSConstants.S_OK;
        }

        // Writes a dump to a file.
        public int WriteDump(enum_DUMPTYPE DUMPTYPE, string pszDumpUrl) {
            return VSConstants.E_NOTIMPL;
        }

        #endregion

        #region IDebugProgram3 Members

        // ExecuteOnThread is called when the SDM wants execution to continue and have 
        // stepping state cleared.  See http://msdn.microsoft.com/en-us/library/bb145596.aspx for a
        // description of different ways we can resume.
        public int ExecuteOnThread(IDebugThread2 pThread) {
            if (_adapterHostProgram == null) {
                return VSConstants.E_NOTIMPL;
            }
            return _adapterHostProgram.ExecuteOnThread(pThread);
        }

        #endregion

        #region IDebugSymbolSettings100 members

        public int SetSymbolLoadState(int bIsManual, int bLoadAdjacent, string strIncludeList, string strExcludeList) {
            // The SDM will call this method on the debug engine when it is created, to notify it of the user's
            // symbol settings in Tools->Options->Debugging->Symbols.
            //
            // Params:
            // bIsManual: true if 'Automatically load symbols: Only for specified modules' is checked
            // bLoadAdjacent: true if 'Specify modules'->'Always load symbols next to the modules' is checked
            // strIncludeList: semicolon-delimited list of modules when automatically loading 'Only specified modules'
            // strExcludeList: semicolon-delimited list of modules when automatically loading 'All modules, unless excluded'

            return VSConstants.S_OK;
        }

        #endregion

        #region Deprecated interface methods
        // These methods are not called by the Visual Studio debugger, so they don't need to be implemented

        int IDebugEngine2.EnumPrograms(out IEnumDebugPrograms2 programs) {
            Debug.Fail("This function is not called by the debugger");

            programs = null;
            return VSConstants.E_NOTIMPL;
        }

        public int Attach(IDebugEventCallback2 pCallback) {
            Debug.Fail("This function is not called by the debugger");

            return VSConstants.E_NOTIMPL;
        }

        public int GetProcess(out IDebugProcess2 process) {
            Debug.Fail("This function is not called by the debugger");

            process = null;
            return VSConstants.E_NOTIMPL;
        }

        public int Execute() {
            Debug.Fail("This function is not called by the debugger.");
            return VSConstants.E_NOTIMPL;
        }

        #endregion

        #region Events

        internal void Send(IDebugEvent2 eventObject, string iidEvent, IDebugProgram2 program, IDebugThread2 thread) {
            uint attributes;
            Guid riidEvent = new Guid(iidEvent);

            EngineUtils.RequireOk(eventObject.GetAttributes(out attributes));

            var events = _events;
            if (events == null) {
                // Probably racing with the end of the process.
                Debug.Fail("_events is null");
                return;
            }

            Debug.WriteLine(String.Format("Sending Event: {0} {1}", eventObject.GetType(), iidEvent));
            try {
                EngineUtils.RequireOk(events.Event(this, null, program, thread, eventObject, ref riidEvent, attributes));
            } catch (InvalidCastException) {
                // COM object has gone away
            }
        }

        internal void Send(IDebugEvent2 eventObject, string iidEvent, IDebugThread2 thread) {
            Send(eventObject, iidEvent, this, thread);
        }

        #endregion
    }
}
