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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.PythonTools.Editor.Formatting;
using TestUtilities;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace PythonToolsTests {
    [TestClass]
    public class PythonFormatterTests {
        [TestMethod, Priority(0)]
        public async Task FormatDocumentYapf() {
            var formatter = new PythonFormatterYapf();
            var interpreterExePath = CreateVirtualEnv(formatter);

            var contents = @"a  = [0,  2, 3  ]
b =100 *2
";
            var filePath = CreateDocument(contents);

            var actual = await formatter.FormatDocumentAsync(interpreterExePath, filePath, contents, null, new string[0]);
            var expected = new TextEdit[] {
                new TextEdit() {
                    NewText = "a = [0, 2, 3]\r\nb = 100 * 2\r\n",
                    Range = new Range() {
                        Start = new Position(0, 0),
                        End = new Position(2, 0),
                    }
                }
            };

            AssertTextEdits(actual, expected);
        }

        [TestMethod, Priority(0)]
        public async Task FormatDocumentAutopep8() {
            var formatter = new PythonFormatterAutopep8();
            var interpreterExePath = CreateVirtualEnv(formatter);

            var contents = @"a  = [0,  2, 3  ]
b =100 *2
";
            var filePath = CreateDocument(contents);

            var actual = await formatter.FormatDocumentAsync(interpreterExePath, filePath, contents, null, new string[0]);
            var expected = new TextEdit[] {
                new TextEdit() {
                    NewText = "a = [0,  2, 3]\r\nb = 100 * 2\r\n",
                    Range = new Range() {
                        Start = new Position(0, 0),
                        End = new Position(2, 0),
                    }
                }
            };

            AssertTextEdits(actual, expected);
        }

        [TestMethod, Priority(0)]
        public async Task FormatDocumentBlack() {
            var formatter = new PythonFormatterBlack();
            var interpreterExePath = CreateVirtualEnv(formatter);

            var contents = @"a  = [0,  2, 3  ]
b =100 *2
";
            var filePath = CreateDocument(contents);

            var actual = await formatter.FormatDocumentAsync(interpreterExePath, filePath, contents, null, new string[0]);
            var expected = new TextEdit[] {
                new TextEdit() {
                    NewText = "\r\na = [0, 2, 3]\r\nb = 100 * 2",
                    Range = new Range() {
                        Start = new Position(0, 0),
                        End = new Position(1, 9),
                    }
                }
            };

            AssertTextEdits(actual, expected);
        }

        [TestMethod, Priority(0)]
        [ExpectedException(typeof(PythonFormatterRangeNotSupportedException))]
        public async Task FormatSelectionBlack() {
            var formatter = new PythonFormatterBlack();
            var interpreterExePath = CreateVirtualEnv(formatter);

            var contents = @"a  = [0,  2, 3  ]
b =100 *2
";
            var filePath = CreateDocument(contents);

            var range = new Range() {
                Start = new Position(0, 0),
                End = new Position(1, 0),
            };

            await formatter.FormatDocumentAsync(interpreterExePath, filePath, contents, range, new string[0]);
        }

        private static string CreateDocument(string contents) {
            var filePath = Path.Combine(TestData.GetTempPath(), "input.py");
            File.WriteAllText(filePath, contents);
            return filePath;
        }

        private static string CreateVirtualEnv(IPythonFormatter formatter) {
            var python = PythonPaths.LatestVersion;
            python.AssertInstalled();

            var envPath = python.CreateVirtualEnv(VirtualEnvName.First, new[] { formatter.Package });

            return Path.Combine(envPath, "scripts", "python.exe");
        }

        private static void AssertTextEdits(TextEdit[] actual, TextEdit[] expected) {
            Assert.AreEqual(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) {
                Assert.AreEqual(expected[i].Range, actual[i].Range);
                Assert.AreEqual(expected[i].NewText, actual[i].NewText);
            }
        }
    }
}
