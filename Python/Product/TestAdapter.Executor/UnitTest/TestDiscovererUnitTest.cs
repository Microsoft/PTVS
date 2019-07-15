﻿using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.TestAdapter.Config;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;


namespace Microsoft.PythonTools.TestAdapter.Services {
    internal class TestDiscovererUnitTest : IPythonTestDiscoverer {
        private readonly PythonProjectSettings _settings;
        private IMessageLogger _logger;
        private static readonly string DiscoveryAdapterPath = PythonToolsInstallPath.GetFile("PythonFiles\\testing_tools\\run_adapter.py");

        public TestDiscovererUnitTest(PythonProjectSettings settings) {
            _settings = settings;
        }

        public void DiscoverTests(IEnumerable<string> sources, IMessageLogger logger, ITestCaseDiscoverySink discoverySink) {
            _logger = logger;
            throw new NotImplementedException("TestExecutorUnitTest::DiscoverTests");

            //var discoveryResults = new List<PytestDiscoveryResults>();

            //try {
            //    var env = InitializeEnvironment(sources, _settings);
            //    var arguments = GetArguments(sources);

            //    using (var outputStream = new MemoryStream())
            //    using (var writer = new StreamWriter(outputStream, encoding: new UTF8Encoding(true), 4096, leaveOpen: true))
            //    using (var proc = ProcessOutput.Run(
            //        _settings.InterpreterPath,
            //        arguments,
            //        _settings.WorkingDirectory,
            //        env,
            //        visible: false,
            //        new StreamRedirector(writer)
            //    )) {

            //        DebugInfo("cd " + _settings.WorkingDirectory);
            //        DebugInfo("set " + _settings.PathEnv + "=" + env[_settings.PathEnv]);
            //        DebugInfo(proc.Arguments);

            //        // If there's an error in the launcher script,
            //        // it will terminate without connecting back.
            //        WaitHandle.WaitAny(new WaitHandle[] { proc.WaitHandle });

            //        outputStream.Flush();
            //        outputStream.Seek(0, SeekOrigin.Begin);
            //        var json = new StreamReader(outputStream).ReadToEnd();

            //        try {
            //            discoveryResults = JsonConvert.DeserializeObject<List<PytestDiscoveryResults>>(json);
            //        } catch (InvalidOperationException ex) {
            //            Error("Failed to parse: {0}".FormatInvariant(ex.Message));
            //            Error(json);
            //        } catch (JsonException ex) {
            //            Error("Failed to parse: {0}".FormatInvariant(ex.Message));
            //            Error(json);
            //        }
            //    }
            //} catch (Exception ex) {
            //    Error(ex.Message);
            //}
        }

        public string[] GetArguments(IEnumerable<string> sources) {
            var arguments = new List<string>();
            arguments.Add(DiscoveryAdapterPath);
            arguments.Add("discover");
            arguments.Add("unittest");
            arguments.Add("--");

            foreach (var s in sources) {
                arguments.Add(s);
            }
            return arguments.ToArray();
        }

        private Dictionary<string, string> InitializeEnvironment(IEnumerable<string> sources, PythonProjectSettings projSettings) {
            var pythonPathVar = projSettings.PathEnv;
            var pythonPath = GetSearchPaths(sources, projSettings);
            var env = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(pythonPathVar)) {
                env[pythonPathVar] = pythonPath;
            }

            foreach (var envVar in projSettings.Environment) {
                env[envVar.Key] = envVar.Value;
            }

            return env;
        }

        private string GetSearchPaths(IEnumerable<string> sources, PythonProjectSettings settings) {
            var paths = settings.SearchPath;

            HashSet<string> knownModulePaths = new HashSet<string>();
            foreach (var source in sources) {
                string testFilePath = PathUtils.GetAbsoluteFilePath(settings.ProjectHome, source);
                var modulePath = ModulePath.FromFullPath(testFilePath);
                if (knownModulePaths.Add(modulePath.LibraryPath)) {
                    paths.Insert(0, modulePath.LibraryPath);
                }
            }

            paths.Insert(0, settings.WorkingDirectory);

            string searchPaths = string.Join(
                ";",
                paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            );
            return searchPaths;
        }

        [Conditional("DEBUG")]
        private void DebugInfo(string message) {
            _logger?.SendMessage(TestMessageLevel.Informational, message);
        }

        private void Error(string message) {
            _logger?.SendMessage(TestMessageLevel.Error, message);
        }
    }
}
