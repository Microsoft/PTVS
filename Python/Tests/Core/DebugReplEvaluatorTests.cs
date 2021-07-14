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
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Debugger;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Repl;
using Microsoft.VisualStudio.InteractiveWindow.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestUtilities;
using TestUtilities.Mocks;
using TestUtilities.Python;

namespace PythonToolsTests {
    public abstract class DebugReplEvaluatorTests {
        private PythonDebugReplEvaluator _evaluator;
        private MockReplWindow _window;

        [ClassInitialize]
        public static void DoDeployment(TestContext context) {
            AssertListener.Initialize();
        }

        internal virtual string DebuggerTestPath {
            get {
                return TestData.GetPath(@"TestData\DebuggerProject\");
            }
        }

        internal virtual PythonVersion Version {
            get {
                return PythonPaths.Python27 ?? PythonPaths.Python27_x64;
            }
        }

        [TestInitialize]
        public void TestInit() {
            Version.AssertInstalled();
            var serviceProvider = PythonToolsTestUtilities.CreateMockServiceProvider();
            _evaluator = new PythonDebugReplEvaluator(serviceProvider);
            _window = new MockReplWindow(_evaluator);
            _evaluator._Initialize(_window);
        }

        [TestCleanup]
        public void TestClean() {
            if (_window != null) {
                Console.WriteLine("Stdout:");
                Console.Write(_window.Output);
                Console.WriteLine("Stderr:");
                Console.Write(_window.Error);
            }
        }

        [TestMethod, Priority(UnitTestPriority.P2_FAILING)]
        public async Task DisplayVariables() {
            await AttachAsync("DebugReplTest1.py", 3);

            Assert.AreEqual("hello", ExecuteText("print(a)"));
            Assert.AreEqual("'hello'", ExecuteText("a"));
        }

        [TestMethod, Priority(UnitTestPriority.P3_FAILING)]
        public async Task DisplayFunctionLocalsAndGlobals() {
            await AttachAsync("DebugReplTest2.py", 13);

            Assert.AreEqual("51", ExecuteText("print(innermost_val)"));
            Assert.AreEqual("5", ExecuteText("print(global_val)"));
        }

        [TestMethod, Priority(UnitTestPriority.P3_FAILING)]
        public async Task ErrorInInput() {
            await AttachAsync("DebugReplTest2.py", 13);

            Assert.AreEqual("", ExecuteText("print(does_not_exist)", false));
            Assert.AreEqual(@"Traceback (most recent call last):
  File ""<debug input>"", line 1, in <module>
NameError: name 'does_not_exist' is not defined
".Replace("\r\n", "\n"), _window.Error.Replace("\r\n", "\n"));
        }

        [TestMethod, Priority(UnitTestPriority.P3_FAILING)]
        public async Task ChangeVariables() {
            await AttachAsync("DebugReplTest2.py", 13);

            Assert.AreEqual("", ExecuteText("innermost_val = 1"));
            Assert.AreEqual("1", ExecuteText("print(innermost_val)"));
        }

        [TestMethod, Priority(UnitTestPriority.P3_FAILING)]
        public async Task ChangeVariablesAndRefreshFrames() {
            // This is really a test for PythonProcess' RefreshFramesAsync
            // but it's convenient to have it here, as this is the exact
            // scenario where it's used in the product.
            // We call RefreshFramesAsync multiple times, which validates a bug fix.
            await AttachAsync("DebugReplTest2.py", 13);

            var process = _processes[0];
            var thread = process.GetThreads().FirstOrDefault();

            var variables = thread.Frames[0].Locals.ToArray();
            Assert.AreEqual("innermost_val", variables[0].ChildName);
            Assert.AreEqual("51", variables[0].StringRepr);

            // Refresh before changing anything, local variable should remain the same
            await process.RefreshThreadFramesAsync(thread.Id, TimeoutToken());
            variables = thread.Frames[0].Locals.ToArray();
            Assert.AreEqual("innermost_val", variables[0].ChildName);
            Assert.AreEqual("51", variables[0].StringRepr);

            Assert.AreEqual("", ExecuteText("innermost_val = 1"));
            Assert.AreEqual("1", ExecuteText("print(innermost_val)"));

            // This should now produce an updated local variable
            await process.RefreshThreadFramesAsync(thread.Id, TimeoutToken());
            variables = thread.Frames[0].Locals.ToArray();
            Assert.AreEqual("innermost_val", variables[0].ChildName);
            Assert.AreEqual("1", variables[0].StringRepr);
        }

        [TestMethod, Priority(UnitTestPriority.P1_FAILING)]
        public async Task AvailableScopes() {
            await AttachAsync("DebugReplTest1.py", 3);

            var expectedData = new Dictionary<string, string>() {
                { "<Current Frame>", null },
                { "abc", "abc" },
                { "dis", "dis" },
            };

            Assert.IsTrue(_evaluator.EnableMultipleScopes);

            var scopes = _evaluator.GetAvailableScopes().ToArray();
            foreach (var expectedItem in expectedData) {
                CollectionAssert.Contains(scopes, expectedItem.Key);
            }

            var scopesAndPaths = _evaluator.GetAvailableScopesAndPaths().ToArray();
            foreach (var expectedItem in expectedData) {
                var actualItem = scopesAndPaths.SingleOrDefault(d => d.Key == expectedItem.Key);
                Assert.IsNotNull(actualItem);
                if (!string.IsNullOrEmpty(expectedItem.Value)) {
                    Assert.IsTrue(PathUtils.IsSamePath(Path.Combine(Version.PrefixPath, "lib", expectedItem.Value), Path.ChangeExtension(actualItem.Value, null)));
                } else {
                    Assert.IsNull(actualItem.Value);
                }
            }
        }

        [TestMethod, Priority(UnitTestPriority.P1_FAILING)]
        public virtual async Task ChangeModule() {
            await AttachAsync("DebugReplTest1.py", 3);

            Assert.AreEqual("'hello'", ExecuteText("a"));

            // Change to the dis module
            Assert.AreEqual("Current module changed to dis", ExecuteCommand(new SwitchModuleCommand(), "dis"));
            Assert.AreEqual("dis", _evaluator.CurrentScopeName);
            Assert.IsTrue(PathUtils.IsSamePath(Path.ChangeExtension(_evaluator.CurrentScopePath, null), Path.Combine(Version.PrefixPath, "lib", "dis")));
            Assert.AreEqual("", ExecuteText("test = 'world'"));
            Assert.AreEqual("'world'", ExecuteText("test"));

            // Change back to the current frame (using localized name)
            Assert.AreEqual("Current module changed to <Current Frame>", ExecuteCommand(new SwitchModuleCommand(), "<Current Frame>"));
            Assert.AreEqual("<Current Frame>", _evaluator.CurrentScopeName);
            Assert.AreEqual("", _evaluator.CurrentScopePath);
            Assert.AreEqual("", ExecuteText("test", false));
            Assert.IsTrue(_window.Error.Contains("NameError:"));
            Assert.AreEqual("'hello'", ExecuteText("a"));

            // Change back to the current frame (using fixed and backwards compatible name)
            Assert.AreEqual("Current module changed to <Current Frame>", ExecuteCommand(new SwitchModuleCommand(), "<CurrentFrame>"));
            Assert.AreEqual("<Current Frame>", _evaluator.CurrentScopeName);
            Assert.AreEqual("", _evaluator.CurrentScopePath);
            Assert.AreEqual("", ExecuteText("test", false));
            Assert.IsTrue(_window.Error.Contains("NameError:"));
            Assert.AreEqual("'hello'", ExecuteText("a"));
        }

        [TestMethod, Priority(UnitTestPriority.P1_FAILING)]
        public virtual async Task ChangeFrame() {
            await AttachAsync("DebugReplTest2.py", 13);

            // We are broken in the innermost function
            string stack;
            stack = ExecuteCommand(new DebugReplFramesCommand(), "");
            Assert.IsTrue(stack.StartsWith(@"=> Frame id=0, function=innermost
   Frame id=1, function=inner
   Frame id=2, function=outer
   Frame id=3, function=<module>"));
            Assert.AreEqual("0", ExecuteCommand(new DebugReplFrameCommand(), ""));
            Assert.AreEqual("51", ExecuteText("print(innermost_val)"));

            // Move up the stack to the inner function
            Assert.AreEqual("Current frame changed to 1", ExecuteCommand(new DebugReplFrameUpCommand(), ""));
            stack = ExecuteCommand(new DebugReplFramesCommand(), "");
            Assert.IsTrue(stack.StartsWith(@"   Frame id=0, function=innermost
=> Frame id=1, function=inner
   Frame id=2, function=outer
   Frame id=3, function=<module>"));
            Assert.AreEqual("1", ExecuteCommand(new DebugReplFrameCommand(), ""));
            Assert.AreEqual("50", ExecuteText("print(inner_val)"));

            // Move to frame 2, the outer function
            Assert.AreEqual("Current frame changed to 2", ExecuteCommand(new DebugReplFrameCommand(), "2"));
            Assert.AreEqual("2", ExecuteCommand(new DebugReplFrameCommand(), ""));
            Assert.AreEqual("10", ExecuteText("print(outer_val)"));

            // Move down the stack, back to the inner function
            Assert.AreEqual("Current frame changed to 1", ExecuteCommand(new DebugReplFrameDownCommand(), ""));
            Assert.AreEqual("1", ExecuteCommand(new DebugReplFrameCommand(), ""));
        }

        [TestMethod, Priority(UnitTestPriority.P3_FAILING)]
        [TestCategory("10s")]
        public async Task ChangeThread() {
            await AttachAsync("DebugReplTest3.py", 39);

            var threads = _processes[0].GetThreads();
            PythonThread main = threads.SingleOrDefault(t => t.Frames[0].FunctionName == "threadmain");
            PythonThread worker1 = threads.SingleOrDefault(t => t.Frames[0].FunctionName == "thread1");
            PythonThread worker2 = threads.SingleOrDefault(t => t.Frames[0].FunctionName == "thread2");

            // We are broken in the the main thread
            string text;
            text = ExecuteCommand(new DebugReplThreadsCommand(), "");
            Assert.IsTrue(text.Contains(String.Format("=> Thread id={0}, name=", main.Id)));
            Assert.IsTrue(text.Contains(String.Format("   Thread id={0}, name=", worker1.Id)));
            Assert.IsTrue(text.Contains(String.Format("   Thread id={0}, name=", worker2.Id)));
            Assert.AreEqual(main.Id.ToString(), ExecuteCommand(new DebugReplThreadCommand(), ""));
            Assert.AreEqual("False", ExecuteText("t1_done"));
            Assert.AreEqual("False", ExecuteText("t2_done"));

            // Switch to worker thread 1
            Assert.AreEqual(String.Format("Current thread changed to {0}, frame 0", worker1.Id), ExecuteCommand(new DebugReplThreadCommand(), worker1.Id.ToString()));
            text = ExecuteCommand(new DebugReplThreadsCommand(), "");
            Assert.IsTrue(text.Contains(String.Format("   Thread id={0}, name=", main.Id)));
            Assert.IsTrue(text.Contains(String.Format("=> Thread id={0}, name=", worker1.Id)));
            Assert.IsTrue(text.Contains(String.Format("   Thread id={0}, name=", worker2.Id)));
            Assert.AreEqual(worker1.Id.ToString(), ExecuteCommand(new DebugReplThreadCommand(), ""));
            Assert.AreEqual("'thread1'", ExecuteText("t1_val"));
        }

        [TestMethod, Priority(UnitTestPriority.P1_FAILING)]
        public virtual async Task ChangeProcess() {
            await AttachAsync("DebugReplTest4A.py", 3);
            await AttachAsync("DebugReplTest4B.py", 3);

            PythonProcess proc1 = _processes[0];
            PythonProcess proc2 = _processes[1];

            // We are broken in process 2 (the last one attached is the current one)
            string text;
            text = ExecuteCommand(new DebugReplProcessesCommand(), "");
            Assert.AreEqual(String.Format(@"   Process id={0}, Language version={2}
=> Process id={1}, Language version={2}", proc1.Id, proc2.Id, Version.Version), text);

            // Switch to process 1
            Assert.AreEqual(String.Format("Current process changed to {0}", proc1.Id), ExecuteCommand(new DebugReplProcessCommand(), proc1.Id.ToString()));
            Assert.AreEqual(String.Format("{0}", proc1.Id), ExecuteCommand(new DebugReplProcessCommand(), String.Empty));
            Assert.AreEqual("'hello'", ExecuteText("a1"));
            Assert.AreEqual("30", ExecuteText("b1"));

            // Switch to process 2
            Assert.AreEqual(String.Format("Current process changed to {0}", proc2.Id), ExecuteCommand(new DebugReplProcessCommand(), proc2.Id.ToString()));
            Assert.AreEqual(String.Format("{0}", proc2.Id), ExecuteCommand(new DebugReplProcessCommand(), String.Empty));
            Assert.AreEqual("'world'", ExecuteText("a2"));
            Assert.AreEqual("60", ExecuteText("b2"));
        }

        [TestMethod, Priority(UnitTestPriority.P3_FAILING)]
        [TestCategory("10s")]
        public async Task Abort() {
            await AttachAsync("DebugReplTest5.py", 3);

            _window.ClearScreen();
            var execute = _evaluator.ExecuteText("for i in range(0,20): time.sleep(0.5)");
            _evaluator.AbortExecution();
            execute.Wait();
            Assert.IsTrue(execute.Result.IsSuccessful);
            Assert.AreEqual("Abort is not supported.", _window.Error.TrimEnd());
        }

        [TestMethod, Priority(UnitTestPriority.P1_FAILING)]
        public async Task StepInto() {
            // Make sure that we don't step into the internal repl code
            // http://pytools.codeplex.com/workitem/777
            await AttachAsync("DebugReplTest6.py", 2);

            var thread = _processes[0].GetThreads()[0];
            await thread.StepIntoAsync(TimeoutToken());

            // Result of step into is not immediate
            Thread.Sleep(1000);

            // We should still be in the <module>, not in the internals of print in repl code
            foreach (var frame in thread.Frames) {
                Console.WriteLine("{0}:{1} [{2}]", frame.FunctionName, frame.LineNo, frame.FileName);
            }
            Assert.AreEqual(1, thread.Frames.Count);
            Assert.AreEqual("<module>", thread.Frames[0].FunctionName);
        }

        private string ExecuteCommand(IInteractiveWindowCommand cmd, string args) {
            _window.ClearScreen();
            var execute = cmd.Execute(_window, args);
            execute.Wait();
            Assert.IsTrue(execute.Result.IsSuccessful);
            return _window.Output.TrimEnd();
        }

        private string ExecuteText(string executionText) {
            return ExecuteText(executionText, true);
        }

        private string ExecuteText(string executionText, bool expectSuccess) {
            _window.ClearScreen();
            var execute = _evaluator.ExecuteText(executionText);
            execute.Wait();
            Assert.AreEqual(expectSuccess, execute.Result.IsSuccessful);
            return _window.Output.TrimEnd();
        }

        private void SafeSetEvent(AutoResetEvent evt) {
            try {
                evt.Set();
            } catch (ObjectDisposedException) {
            }
        }

        private async Task AttachAsync(string filename, int lineNo) {
            var debugger = new PythonDebugger();
            PythonProcess process = debugger.DebugProcess(Version, DebuggerTestPath + filename, null, async (newproc, newthread) => {
                var breakPoint = newproc.AddBreakpointByFileExtension(lineNo, filename);
                await breakPoint.AddAsync(TimeoutToken());
            });

            _processes.Add(process);

            long? threadAtBreakpoint = null;

            using (var brkHit = new AutoResetEvent(false))
            using (var procExited = new AutoResetEvent(false)) {
                EventHandler<BreakpointHitEventArgs> breakpointHitHandler = (s, e) => {
                    threadAtBreakpoint = e.Thread.Id;
                    SafeSetEvent(brkHit);
                };
                EventHandler<ProcessExitedEventArgs> processExitedHandler = (s, e) => SafeSetEvent(procExited);
                process.BreakpointHit += breakpointHitHandler;
                process.ProcessExited += processExitedHandler;

                try {
                    await process.StartAsync();
                } catch (Win32Exception ex) {
                    _processes.Remove(process);
                    if (ex.HResult == -2147467259 /*0x80004005*/) {
                        Assert.Inconclusive("Required Python interpreter is not installed");
                    } else {
                        Assert.Fail("Process start failed:\r\n" + ex.ToString());
                    }
                }

                var handles = new[] { brkHit, procExited };
                if (WaitHandle.WaitAny(handles, 25000) != 0) {
                    Assert.Fail("Failed to wait on event");
                }

                process.BreakpointHit -= breakpointHitHandler;
                process.ProcessExited -= processExitedHandler;
            }

            await _evaluator.AttachProcessAsync(process, new MockThreadIdMapper());

            // AttachProcessAsync calls InitializeAsync which sets the active
            // thread by using the DTE (which is null in these tests), so we
            // adjust it to the correct thread where breakpoint was hit.
            if (threadAtBreakpoint != null) {
                _evaluator.ChangeActiveThread(threadAtBreakpoint.Value, false);
            }
        }

        protected static CancellationToken TimeoutToken() {
            return CancellationTokens.After5s;
        }
    }

    [TestClass]
    public class DebugReplEvaluatorTests35 : DebugReplEvaluatorTests {
        [ClassInitialize]
        public static new void DoDeployment(TestContext context) {
            AssertListener.Initialize();
        }

        internal override PythonVersion Version {
            get {
                return PythonPaths.Python35 ?? PythonPaths.Python35_x64;
            }
        }
    }

    [TestClass]
    public class DebugReplEvaluatorTests36 : DebugReplEvaluatorTests {
        [ClassInitialize]
        public static new void DoDeployment(TestContext context) {
            AssertListener.Initialize();
        }

        internal override PythonVersion Version {
            get {
                return PythonPaths.Python36 ?? PythonPaths.Python36_x64;
            }
        }
    }

    [TestClass]
    public class DebugReplEvaluatorTests37 : DebugReplEvaluatorTests {
        [ClassInitialize]
        public static new void DoDeployment(TestContext context) {
            AssertListener.Initialize();
        }

        internal override PythonVersion Version {
            get {
                return PythonPaths.LatestVersion;
            }
        }
    }

    [TestClass]
    public class DebugReplEvaluatorTests27 : DebugReplEvaluatorTests {
        [ClassInitialize]
        public static new void DoDeployment(TestContext context) {
            AssertListener.Initialize();
        }

        internal override PythonVersion Version {
            get {
                return PythonPaths.Python27 ?? PythonPaths.Python27_x64;
            }
        }
    }
}
