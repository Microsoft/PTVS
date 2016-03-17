﻿// Python Tools for Visual Studio
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
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Language;
using Microsoft.PythonTools.Options;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Project;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.InteractiveWindow.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudioTools;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.PythonTools.Repl {
    [InteractiveWindowRole("Execution")]
    [InteractiveWindowRole("Reset")]
    [ContentType(PythonCoreConstants.ContentType)]
    [ContentType(PredefinedInteractiveCommandsContentTypes.InteractiveCommandContentTypeName)]
    partial class PythonInteractiveEvaluator :
        IPythonInteractiveEvaluator,
        IMultipleScopeEvaluator,
        IPythonInteractiveIntellisense, 
        IDisposable
    {
        protected readonly IServiceProvider _serviceProvider;
        private readonly StringBuilder _deferredOutput;

        protected CommandProcessorThread _thread;
        private IInteractiveWindowCommands _commands;
        private IInteractiveWindow _window;
        private PythonInteractiveOptions _options;
        private VsProjectAnalyzer _analyzer;

        private bool _enableMultipleScopes;
        private IReadOnlyList<string> _availableScopes;

        private bool _isDisposed;

        public PythonInteractiveEvaluator(IServiceProvider serviceProvider) {
            _serviceProvider = serviceProvider;
            _deferredOutput = new StringBuilder();
            EnvironmentVariables = new Dictionary<string, string>();
            _enableMultipleScopes = true;
        }

        protected void Dispose(bool disposing) {
            if (_isDisposed) {
                return;
            }

            _isDisposed = true;
            if (disposing) {
                var thread = Interlocked.Exchange(ref _thread, null);
                if (thread != null) {
                    thread.Dispose();
                    WriteError(Strings.ReplExited);
                }
                _analyzer?.Dispose();
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PythonInteractiveEvaluator() {
            Dispose(false);
        }

        public string DisplayName { get; set; }
        public string ProjectMoniker { get; set; }
        public string InterpreterPath { get; set; }
        public PythonLanguageVersion LanguageVersion { get; set; }
        public string InterpreterArguments {get; set; }
        public string WorkingDirectory { get; set; }
        public IDictionary<string, string> EnvironmentVariables { get; set; }
        public string ScriptsPath { get; set; }

        public bool UseSmartHistoryKeys { get; set; }
        public bool LiveCompletionsOnly { get; set; }

        internal virtual void OnConnected() { }
        internal virtual void OnAttach() { }
        internal virtual void OnDetach() { }

        public VsProjectAnalyzer Analyzer {
            get {
                if (_analyzer != null) {
                    return _analyzer;
                }

                // HACK: We use the interpreter path as the id for the factory
                // Eventually, we will remove factories completely and always
                // use the path as the ID, but right now this is a hack.
                var interpreters = _serviceProvider.GetComponentModel().GetService<IInterpreterOptionsService>();
                var factory = interpreters.Interpreters.FirstOrDefault(
                    f => CommonUtils.IsSamePath(f.Configuration.InterpreterPath, InterpreterPath)
                );
                _analyzer = new VsProjectAnalyzer(_serviceProvider, factory, interpreters.Interpreters.ToArray());
                return _analyzer;
            }
        }

        internal void WriteOutput(string text, bool addNewline = true) {
            var wnd = CurrentWindow;
            if (wnd == null) {
                lock (_deferredOutput) {
                    _deferredOutput.Append(text);
                }
            } else {
                AppendTextWithEscapes(wnd, text, addNewline, isError: false);
            }
        }

        internal void WriteError(string text, bool addNewline = true) {
            var wnd = CurrentWindow;
            if (wnd == null) {
                lock (_deferredOutput) {
                    _deferredOutput.Append(text);
                }
            } else {
                AppendTextWithEscapes(wnd, text, addNewline, isError: true);
            }
        }

        public bool IsDisconnected => !(_thread?.IsConnected ?? false);

        public bool IsExecuting => (_thread?.IsExecuting ?? false);

        public string CurrentScopeName {
            get {
                return (_thread?.IsConnected ?? false) ? _thread.CurrentScope : "<disconnected>";
            }
        }

        public IInteractiveWindow CurrentWindow {
            get {
                return _window;
            }
            set {
                if (_window != null) {
                }
                _commands = null;

                if (value != null) {
                    lock (_deferredOutput) {
                        AppendTextWithEscapes(value, _deferredOutput.ToString(), false, false);
                        _deferredOutput.Clear();
                    }

                    _options = _serviceProvider.GetPythonToolsService().InteractiveOptions;
                    _options.Changed += InteractiveOptions_Changed;
                    UseSmartHistoryKeys = _options.UseSmartHistory;
                    LiveCompletionsOnly = _options.LiveCompletionsOnly;
                } else {
                    if (_options != null) {
                        _options.Changed -= InteractiveOptions_Changed;
                        _options = null;
                    }
                }
                _window = value;
            }
        }

        private async void InteractiveOptions_Changed(object sender, EventArgs e) {
            if (!ReferenceEquals(sender, _options)) {
                return;
            }

            UseSmartHistoryKeys = _options.UseSmartHistory;
            LiveCompletionsOnly = _options.LiveCompletionsOnly;

            var window = CurrentWindow;
            if (window == null) {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            window.TextView.Options.SetOptionValue(InteractiveWindowOptions.SmartUpDown, UseSmartHistoryKeys);
        }

        public bool EnableMultipleScopes {
            get { return _enableMultipleScopes; }
            set {
                if (_enableMultipleScopes != value) {
                    _enableMultipleScopes = value;
                    MultipleScopeSupportChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler<EventArgs> AvailableScopesChanged;
        public event EventHandler<EventArgs> MultipleScopeSupportChanged;

        private async void Thread_AvailableScopesChanged(object sender, EventArgs e) {
            _availableScopes = (await ((CommandProcessorThread)sender).GetAvailableUserScopesAsync(10000))?.ToArray();
            AvailableScopesChanged?.Invoke(this, EventArgs.Empty);
        }

        public IEnumerable<KeyValuePair<string, bool>> GetAvailableScopesAndKind() {
            var t = _thread?.GetAvailableScopesAndKindAsync(1000);
            if (t != null && t.Wait(1000) && t.Result != null) {
                return t.Result;
            }
            return Enumerable.Empty<KeyValuePair<string, bool>>();
        }

        public MemberResult[] GetMemberNames(string text) {
            return _thread?.GetMemberNames(text) ?? new MemberResult[0];
        }

        public OverloadDoc[] GetSignatureDocumentation(string text) {
            return _thread?.GetSignatureDocumentation(text) ?? new OverloadDoc[0];
        }

        public void AbortExecution() {
            _thread?.AbortCommand();
        }

        public bool CanExecuteCode(string text) {
            if (text.EndsWith("\n")) {
                return true;
            }

            using (var parser = Parser.CreateParser(new StringReader(text), LanguageVersion)) {
                ParseResult pr;
                parser.ParseInteractiveCode(out pr);
                if (pr == ParseResult.IncompleteToken || pr == ParseResult.IncompleteStatement) {
                    return false;
                }
            }
            return true;
        }

        protected async Task<CommandProcessorThread> EnsureConnectedAsync() {
            var thread = Volatile.Read(ref _thread);
            if (thread != null) {
                return thread;
            }

            return await _serviceProvider.GetUIThread().InvokeTask(async () => {
                if (!string.IsNullOrEmpty(ProjectMoniker)) {
                    UpdatePropertiesFromProjectMoniker();
                }

                thread = Connect();

                var newerThread = Interlocked.CompareExchange(ref _thread, thread, null);
                if (newerThread != null) {
                    thread.Dispose();
                    return newerThread;
                }

                var scriptsPath = ScriptsPath;
                if (string.IsNullOrEmpty(scriptsPath)) {
                    scriptsPath = GetScriptsPath(null, DisplayName) ??
                        GetScriptsPath(null, LanguageVersion.ToVersion().ToString());
                }

                if (File.Exists(scriptsPath)) {
                    if (!(await ExecuteFileAsync(scriptsPath, null)).IsSuccessful) {
                        WriteError("Error executing " + scriptsPath);
                    }
                } else if (Directory.Exists(scriptsPath)) {
                    foreach (var file in Directory.EnumerateFiles(scriptsPath, "*.py", SearchOption.TopDirectoryOnly)) {
                        if (!(await ExecuteFileAsync(file, null)).IsSuccessful) {
                            WriteError("Error executing " + file);
                        }
                    }
                }

                thread.AvailableScopesChanged += Thread_AvailableScopesChanged;
                return thread;
            });
        }

        internal void UpdatePropertiesFromProjectMoniker() {
            var solution = _serviceProvider.GetService(typeof(SVsSolution)) as IVsSolution;
            if (solution == null) {
                return;
            }

            IVsHierarchy hier;
            if (string.IsNullOrEmpty(ProjectMoniker) ||
                ErrorHandler.Failed(solution.GetProjectOfUniqueName(ProjectMoniker, out hier))) {
                return;
            }
            var pyProj = hier?.GetProject()?.GetPythonProject();
            if (pyProj == null) {
                return;
            }

            var props = PythonProjectLaunchProperties.Create(pyProj);
            if (props == null) {
                return;
            }

            InterpreterPath = props.GetInterpreterPath();
            InterpreterArguments = props.GetInterpreterArguments();
            var version = (pyProj.GetInterpreterFactory()?.Configuration.Version ?? new Version()).ToLanguageVersion();
            LanguageVersion = version;
            WorkingDirectory = props.GetWorkingDirectory();
            EnvironmentVariables = props.GetEnvironment(true);
            ScriptsPath = GetScriptsPath(pyProj.ProjectHome, "Scripts");
        }

        internal string GetScriptsPath(string root, params string[] parts) {
            if (string.IsNullOrEmpty(root)) {
                // TODO: Allow customizing the scripts path
                //root = _serviceProvider.GetPythonToolsService().InteractiveOptions.ScriptsPath;
                if (string.IsNullOrEmpty(root)) {
                    root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    parts = new[] { "Visual Studio " + AssemblyVersionInfo.VSName, "Python Scripts" }
                        .Concat(parts).ToArray();
                }
            }
            if (parts.Length > 0) {
                try {
                    root = CommonUtils.GetAbsoluteDirectoryPath(root, Path.Combine(parts));
                } catch (ArgumentException) {
                    return null;
                }
            }

            if (!Directory.Exists(root)) {
                return null;
            }

            return root;
        }


        public async Task<ExecutionResult> ExecuteCodeAsync(string text) {
            var cmdRes = _commands.TryExecuteCommand();
            if (cmdRes != null) {
                return await cmdRes;
            }

            var thread = await EnsureConnectedAsync();
            if (thread != null) {
                return await thread.ExecuteText(text);
            }

            WriteError(Strings.ReplDisconnected);
            return ExecutionResult.Failure;
        }

        public async Task<ExecutionResult> ExecuteFileAsync(string filename, string extraArgs) {
            var thread = await EnsureConnectedAsync();
            if (thread != null) {
                return await thread.ExecuteFile(filename, extraArgs, "script");
            }

            WriteError(Strings.ReplDisconnected);
            return ExecutionResult.Failure;
        }

        public async Task<ExecutionResult> ExecuteModuleAsync(string name, string extraArgs) {
            var thread = await EnsureConnectedAsync();
            if (thread != null) {
                return await thread.ExecuteFile(name, extraArgs, "module");
            }

            WriteError(Strings.ReplDisconnected);
            return ExecutionResult.Failure;
        }

        public async Task<ExecutionResult> ExecuteProcessAsync(string filename, string extraArgs) {
            var thread = await EnsureConnectedAsync();
            if (thread != null) {
                return await thread.ExecuteFile(filename, extraArgs, "process");
            }

            WriteError(Strings.ReplDisconnected);
            return ExecutionResult.Failure;
        }

        const string _splitRegexPattern = @"(?x)\s*,\s*(?=(?:[^""]*""[^""]*"")*[^""]*$)"; // http://regexhero.net/library/52/
        private static Regex _splitLineRegex = new Regex(_splitRegexPattern);

        public string FormatClipboard() {
            if (Clipboard.ContainsData(DataFormats.CommaSeparatedValue)) {
                string data = Clipboard.GetData(DataFormats.CommaSeparatedValue) as string;
                if (data != null) {
                    string[] lines = data.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    StringBuilder res = new StringBuilder();
                    res.AppendLine("[");
                    foreach (var line in lines) {
                        string[] items = _splitLineRegex.Split(line);

                        res.Append("  [");
                        for (int i = 0; i < items.Length; i++) {
                            res.Append(FormatItem(items[i]));

                            if (i != items.Length - 1) {
                                res.Append(", ");
                            }
                        }
                        res.AppendLine("],");
                    }
                    res.AppendLine("]");
                    return res.ToString();
                }
            }
            return EditFilter.RemoveReplPrompts(
                _serviceProvider.GetPythonToolsService(),
                Clipboard.GetText(),
                _window.TextView.Options.GetNewLineCharacter()
            );
        }

        private static string FormatItem(string item) {
            if (String.IsNullOrWhiteSpace(item)) {
                return "None";
            }
            double doubleVal;
            int intVal;
            if (Double.TryParse(item, out doubleVal) ||
                Int32.TryParse(item, out intVal)) {
                return item;
            }

            if (item[0] == '"' && item[item.Length - 1] == '"' && item.IndexOf(',') != -1) {
                // remove outer quotes, remove "" escaping
                item = item.Substring(1, item.Length - 2).Replace("\"\"", "\"");
            }

            // put in single quotes and escape single quotes and backslashes
            return "'" + item.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        public IEnumerable<string> GetAvailableScopes() {
            return _availableScopes ?? Enumerable.Empty<string>();
        }

        public void SetScope(string scopeName) {
            _thread?.SetScope(scopeName);
        }

        public string GetPrompt() {
            if ((_window?.CurrentLanguageBuffer.CurrentSnapshot.LineCount ?? 1) > 1) {
                return SecondaryPrompt;
            } else {
                return PrimaryPrompt;
            }
        }

        internal string PrimaryPrompt => _thread?.PrimaryPrompt ?? ">>> ";
        internal string SecondaryPrompt => _thread?.SecondaryPrompt ?? "... ";

        public async Task<ExecutionResult> InitializeAsync() {
            if (_commands != null) {
                // Already initialized
                return ExecutionResult.Success;
            }

            var msg = Strings.ReplInitializationMessage.FormatUI(
                DisplayName,
                AssemblyVersionInfo.Version,
                AssemblyVersionInfo.VSVersion
            ).Replace("&#x1b;", "\x1b");

            WriteOutput(msg, addNewline: true);

            _window.TextView.BufferGraph.GraphBuffersChanged += BufferGraphGraphBuffersChanged;

            _window.TextView.Options.SetOptionValue(InteractiveWindowOptions.SmartUpDown, UseSmartHistoryKeys);
            _commands = GetInteractiveCommands(_serviceProvider, _window, this);

            return ExecutionResult.Success;
        }

        private void BufferGraphGraphBuffersChanged(object sender, GraphBuffersChangedEventArgs e) {
            foreach (var removed in e.RemovedBuffers) {
                BufferParser parser;
                if (removed.Properties.TryGetProperty(typeof(BufferParser), out parser)) {
                    parser.RemoveBuffer(removed);
                }
            }
        }

        public Task<ExecutionResult> ResetAsync(bool initialize = true) {
            return ResetWorkerAsync(initialize, false);
        }

        public Task<ExecutionResult> ResetAsync(bool initialize, bool quiet) {
            return ResetWorkerAsync(initialize, quiet);
        }

        private async Task<ExecutionResult> ResetWorkerAsync(bool initialize, bool quiet) {
            // suppress reporting "failed to launch repl" process
            var thread = Interlocked.Exchange(ref _thread, null);
            if (thread == null) {
                if (!quiet) {
                    WriteError(Strings.ReplNotStarted);
                }
                return ExecutionResult.Success;
            }

            foreach (var buffer in CurrentWindow.TextView.BufferGraph.GetTextBuffers(b => b.ContentType.IsOfType(PythonCoreConstants.ContentType))) {
                buffer.Properties[ParseQueue.DoNotParse] = ParseQueue.DoNotParse;
            }

            if (!quiet) {
                WriteOutput(Strings.ReplReset);
            }

            thread.IsProcessExpectedToExit = quiet;
            thread.Dispose();

            var options = _serviceProvider.GetPythonToolsService().InteractiveOptions;
            UseSmartHistoryKeys = options.UseSmartHistory;
            LiveCompletionsOnly = options.LiveCompletionsOnly;

            return ExecutionResult.Success;
        }

        internal Task InvokeAsync(Action action) {
            return ((System.Windows.UIElement)_window).Dispatcher.InvokeAsync(action).Task;
        }

        internal void WriteFrameworkElement(System.Windows.UIElement control, System.Windows.Size desiredSize) {
            if (_window == null) {
                return;
            }

            _window.Write("");
            _window.FlushOutput();

            var caretPos = _window.TextView.Caret.Position.BufferPosition;
            var manager = InlineReplAdornmentProvider.GetManager(_window.TextView);
            manager.AddAdornment(new ZoomableInlineAdornment(control, _window.TextView, desiredSize), caretPos);
        }


        internal static IInteractiveWindowCommands GetInteractiveCommands(
            IServiceProvider serviceProvider,
            IInteractiveWindow window,
            IInteractiveEvaluator eval
        ) {
            var model = serviceProvider.GetComponentModel();
            var cmdFactory = model.GetService<IInteractiveWindowCommandsFactory>();
            var cmds = model.GetExtensions<IInteractiveWindowCommand>();
            var roles = eval.GetType()
                .GetCustomAttributes(typeof(InteractiveWindowRoleAttribute), true)
                .Select(r => ((InteractiveWindowRoleAttribute)r).Name)
                .ToArray();

            var contentTypeRegistry = model.GetService<IContentTypeRegistryService>();
            var contentTypes = eval.GetType()
                .GetCustomAttributes(typeof(ContentTypeAttribute), true)
                .Select(r => contentTypeRegistry.GetContentType(((ContentTypeAttribute)r).ContentTypes))
                .ToArray();

            return cmdFactory.CreateInteractiveCommands(
                window,
                "$",
                cmds.Where(x => IsCommandApplicable(x, roles, contentTypes))
            );
        }

        private static bool IsCommandApplicable(
            IInteractiveWindowCommand command,
            string[] supportedRoles,
            IContentType[] supportedContentTypes
        ) {
            var commandRoles = command.GetType().GetCustomAttributes(typeof(InteractiveWindowRoleAttribute), true).Select(r => ((InteractiveWindowRoleAttribute)r).Name).ToArray();

            // Commands with no roles are always applicable.
            // If a command specifies roles and none apply, exclude it
            if (commandRoles.Any() && !commandRoles.Intersect(supportedRoles).Any()) {
                return false;
            }

            var commandContentTypes = command.GetType()
                .GetCustomAttributes(typeof(ContentTypeAttribute), true)
                .Select(a => ((ContentTypeAttribute)a).ContentTypes)
                .ToArray();

            // Commands with no content type are always applicable
            // If a commands specifies content types and none apply, exclude it
            if (commandContentTypes.Any() && !commandContentTypes.Any(cct => supportedContentTypes.Any(sct => sct.IsOfType(cct)))) {
                return false;
            }

            return true;
        }

        #region Append Text helpers

        private static void AppendTextWithEscapes(
            IInteractiveWindow window,
            string text,
            bool addNewLine,
            bool isError
        ) {
            int start = 0, escape = text.IndexOf("\x1b[");
            var colors = window.OutputBuffer.Properties.GetOrCreateSingletonProperty(
                ReplOutputClassifier.ColorKey,
                () => new List<ColoredSpan>()
            );
            ConsoleColor? color = null;

            Span span;
            var write = isError ? (Func<string, Span>)window.WriteError : window.Write;

            while (escape >= 0) {
                span = write(text.Substring(start, escape - start));
                if (span.Length > 0) {
                    colors.Add(new ColoredSpan(span, color));
                }

                start = escape + 2;
                color = GetColorFromEscape(text, ref start);
                escape = text.IndexOf("\x1b[", start);
            }

            var rest = text.Substring(start);
            if (addNewLine) {
                rest += Environment.NewLine;
            }

            span = write(rest);
            if (span.Length > 0) {
                colors.Add(new ColoredSpan(span, color));
            }
        }

        private static ConsoleColor Change(ConsoleColor? from, ConsoleColor to) {
            return ((from ?? ConsoleColor.Black) & ConsoleColor.DarkGray) | to;
        }

        private static ConsoleColor? GetColorFromEscape(string text, ref int start) {
            // http://en.wikipedia.org/wiki/ANSI_escape_code
            // process any ansi color sequences...
            ConsoleColor? color = null;
            List<int> codes = new List<int>();
            int? value = 0;

            while (start < text.Length) {
                if (text[start] >= '0' && text[start] <= '9') {
                    // continue parsing the integer...
                    if (value == null) {
                        value = 0;
                    }
                    value = 10 * value.Value + (text[start] - '0');
                } else if (text[start] == ';') {
                    if (value != null) {
                        codes.Add(value.Value);
                        value = null;
                    } else {
                        // CSI ; - invalid or CSI ### ;;, both invalid
                        break;
                    }
                } else if (text[start] == 'm') {
                    start += 1;
                    if (value != null) {
                        codes.Add(value.Value);
                    }

                    // parsed a valid code
                    if (codes.Count == 0) {
                        // reset
                        color = null;
                    } else {
                        for (int j = 0; j < codes.Count; j++) {
                            switch (codes[j]) {
                                case 0: color = ConsoleColor.White; break;
                                case 1: // bright/bold
                                    color |= ConsoleColor.DarkGray;
                                    break;
                                case 2: // faint

                                case 3: // italic
                                case 4: // single underline
                                    break;
                                case 5: // blink slow
                                case 6: // blink fast
                                    break;
                                case 7: // negative
                                case 8: // conceal
                                case 9: // crossed out
                                case 10: // primary font
                                case 11: // 11-19, n-th alternate font
                                    break;
                                case 21: // bright/bold off 
                                    color &= ~ConsoleColor.DarkGray;
                                    break;
                                case 22: // normal intensity
                                case 24: // underline off
                                    break;
                                case 25: // blink off
                                    break;
                                case 27: // image - postive
                                case 28: // reveal
                                case 29: // not crossed out
                                case 30: color = Change(color, ConsoleColor.Black); break;
                                case 31: color = Change(color, ConsoleColor.DarkRed); break;
                                case 32: color = Change(color, ConsoleColor.DarkGreen); break;
                                case 33: color = Change(color, ConsoleColor.DarkYellow); break;
                                case 34: color = Change(color, ConsoleColor.DarkBlue); break;
                                case 35: color = Change(color, ConsoleColor.DarkMagenta); break;
                                case 36: color = Change(color, ConsoleColor.DarkCyan); break;
                                case 37: color = Change(color, ConsoleColor.Gray); break;
                                case 38: // xterm 286 background color
                                case 39: // default text color
                                    color = null;
                                    break;
                                case 40: // background colors
                                case 41:
                                case 42:
                                case 43:
                                case 44:
                                case 45:
                                case 46:
                                case 47: break;
                                case 90: color = ConsoleColor.DarkGray; break;
                                case 91: color = ConsoleColor.Red; break;
                                case 92: color = ConsoleColor.Green; break;
                                case 93: color = ConsoleColor.Yellow; break;
                                case 94: color = ConsoleColor.Blue; break;
                                case 95: color = ConsoleColor.Magenta; break;
                                case 96: color = ConsoleColor.Cyan; break;
                                case 97: color = ConsoleColor.White; break;
                            }
                        }
                    }
                    break;
                } else {
                    // unknown char, invalid escape
                    break;
                }
                start += 1;
            }
            return color;
        }

        #endregion

        #region Compatibility

        private static Version _vsInteractiveVersion;

        internal static Version VSInteractiveVersion {
            get {
                if (_vsInteractiveVersion == null) {
                    _vsInteractiveVersion = typeof(IInteractiveWindow).Assembly
                        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                        .Select(a => { Version v; return Version.TryParse(a.InformationalVersion, out v) ? v : null; })
                        .FirstOrDefault(v => v != null) ?? new Version();
                }
                return _vsInteractiveVersion;
            }
        }


        #endregion
    }

    internal static class PythonInteractiveEvaluatorExtensions {
        public static PythonInteractiveEvaluator GetPythonEvaluator(this IInteractiveWindow window) {
            var pie = window?.Evaluator as PythonInteractiveEvaluator;
            if (pie != null) {
                return pie;
            }

            pie = (window?.Evaluator as SelectableReplEvaluator)?.Evaluator as PythonInteractiveEvaluator;
            return pie;
        }
    }
}
