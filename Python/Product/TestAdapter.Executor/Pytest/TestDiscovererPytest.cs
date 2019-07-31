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
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.TestAdapter.Config;
using Microsoft.PythonTools.TestAdapter.Pytest;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Newtonsoft.Json;

namespace Microsoft.PythonTools.TestAdapter.Services {
    internal class TestDiscovererPytest : IPythonTestDiscoverer {
        private readonly PythonProjectSettings _settings;
        private IMessageLogger _logger;
        private static readonly string DiscoveryAdapterPath = PythonToolsInstallPath.GetFile("PythonFiles\\testing_tools\\run_adapter.py");

        public TestDiscovererPytest(PythonProjectSettings settings) {
            _settings = settings;
        }

        public void DiscoverTests(IEnumerable<string> sources, IMessageLogger logger, ITestCaseDiscoverySink discoverySink) {
            _logger = logger;
            string json = null;

            LogInfo(Strings.PythonTestDiscovererStartedMessage.FormatUI(_settings.DiscoveryWaitTimeInSeconds));
            try {
                json = ExecuteProcess(sources);
            } catch (TimeoutException) {
                Error(Strings.PythonTestDiscovererTimeoutErrorMessage);
                return;
            }

            List<PytestDiscoveryResults> results = null;
            try {
                results = JsonConvert.DeserializeObject<List<PytestDiscoveryResults>>(json);
            } catch (InvalidOperationException ex) {
                Error("Failed to parse: {0}".FormatInvariant(ex.Message));
                Error(json);
            } catch (JsonException ex) {
                Error("Failed to parse: {0}".FormatInvariant(ex.Message));
                Error(json);
            }

            CreateVsTests(results, discoverySink);
        }


        private void CreateVsTests(IEnumerable<PytestDiscoveryResults> discoveryResults, ITestCaseDiscoverySink discoverySink) {
            foreach (PytestDiscoveryResults result in discoveryResults ?? Enumerable.Empty<PytestDiscoveryResults>()) {
                var parentMap =  result.Parents.ToDictionary(p => p.Id, p => p);
                foreach (PytestTest test in result.Tests) {
                    try {
                        TestCase tc = test.ToVsTestCase(_settings.ProjectHome, parentMap);
                        DebugInfo($"{tc.DisplayName} Source:{tc.Source} Line:{tc.LineNumber}");
                        discoverySink?.SendTestCase(tc);
                    } catch (Exception ex) {
                        Error(ex.Message);
                    }
                }
            }
        } 

        private string ExecuteProcess(IEnumerable<string> sources) {
            var env = InitializeEnvironment(sources, _settings);
            var arguments = GetArguments(sources);
            var utf8 = new UTF8Encoding(false);

            using (var outputStream = new MemoryStream())
            using (var reader = new StreamReader(outputStream, utf8, false, 4096, true))
            using (var writer = new StreamWriter(outputStream, encoding: new UTF8Encoding(true), 4096, leaveOpen: true))
            using (var proc = ProcessOutput.Run(
                _settings.InterpreterPath,
                arguments,
                _settings.WorkingDirectory,
                env,
                visible: false,
                new StreamRedirector(writer)
            )) {
                DebugInfo("cd " + _settings.WorkingDirectory);
                DebugInfo("set " + _settings.PathEnv + "=" + env[_settings.PathEnv]);
                DebugInfo(proc.Arguments);

                if (!proc.ExitCode.HasValue) {
                    if (!proc.Wait(TimeSpan.FromSeconds(_settings.DiscoveryWaitTimeInSeconds))) {
                        try {
                            proc.Kill();
                        } catch (InvalidOperationException) {
                            // Process has already exited
                        }
                        throw new TimeoutException();
                    }
                }
                outputStream.Flush();
                outputStream.Seek(0, SeekOrigin.Begin);
                string json = reader.ReadToEnd();
                return json;
            }
        }

        public string[] GetArguments(IEnumerable<string> sources) {
            var arguments = new List<string>();
            arguments.Add(DiscoveryAdapterPath);
            arguments.Add("discover");
            arguments.Add("pytest");
            arguments.Add("--");
            arguments.Add("--cache-clear");

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
            _logger?.SendMessage(TestMessageLevel.Informational, message ?? String.Empty);
        }

        private void LogInfo(string message) {
            _logger?.SendMessage(TestMessageLevel.Informational, message ?? String.Empty);
        }

        private void Error(string message) {
            _logger?.SendMessage(TestMessageLevel.Error, message ?? String.Empty);
        }
    }
}
