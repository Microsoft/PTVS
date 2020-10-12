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
using System.Windows.Forms;
using Microsoft.PythonTools.LanguageServerClient;

namespace Microsoft.PythonTools.Options {
    public partial class PythonAnalysisOptionsControl : UserControl {
        public PythonAnalysisOptionsControl() {
            InitializeComponent();

            _diagnosticsModeCombo.Items.Add(Strings.DiagnosticModeOpenFiles);
            _diagnosticsModeCombo.Items.Add(Strings.DiagnosticModeWorkspace);
            _diagnosticModeToolTip.SetToolTip(_diagnosticsModeCombo, Strings.DiagnosticModeToolTip);

            _typeCheckingMode.Items.Add(Strings.TypeCheckingModeBasic);
            _typeCheckingMode.Items.Add(Strings.TypeCheckingModeStrict);
            _typeCheckingToolTip.SetToolTip(_typeCheckingMode, Strings.TypeCheckingModeToolTip);

            _logLevelCombo.Items.Add(Strings.LogLevelError);
            _logLevelCombo.Items.Add(Strings.LogLevelWarning);
            _logLevelCombo.Items.Add(Strings.LogLevelInformation);
            _logLevelCombo.Items.Add(Strings.LogLevelTrace);
            _logLevelToolTip.SetToolTip(_logLevelCombo, Strings.LogLevelToolTip);

            _stubsPathToolTip.SetToolTip(_stubsPath, Strings.StubPathToolTip);
            
            _searchPathsToolTip.SetToolTip(_searchPaths, Strings.SearchPathsToolTip);
            _searchPathsLabelToolTip.SetToolTip(_searchPathsLabel, Strings.SearchPathsToolTip);

            _typeshedPathsToolTip.SetToolTip(_typeshedPaths, Strings.TypeshedPathsToolTip);
            _typeshedPathsLabelToolTip.SetToolTip(_typeShedPathsLabel, Strings.TypeshedPathsToolTip);
        }

        internal void SyncControlWithPageSettings(PythonToolsService pyService) {
            _stubsPath.Text = pyService.AnalysisOptions.StubPath;

            _searchPaths.Lines = pyService.AnalysisOptions.ExtraPaths ?? Array.Empty<string>();
            _typeshedPaths.Lines = pyService.AnalysisOptions.TypeshedPaths ?? Array.Empty<string>();

            _autoSearchPath.Checked = pyService.AnalysisOptions.AutoSearchPaths;
            _diagnosticsModeCombo.SelectedIndex =
                pyService.AnalysisOptions.DiagnosticMode == PylanceSettings.DiagnosticMode.OpenFilesOnly ? 0 : 1;
            _typeCheckingMode.SelectedIndex =
                pyService.AnalysisOptions.TypeCheckingMode == PylanceSettings.TypeCheckingMode.Basic ? 0 : 1;

            if (pyService.AnalysisOptions.LogLevel == PylanceSettings.LogLevel.Error) {
                _logLevelCombo.SelectedIndex = 0;
            } else if (pyService.AnalysisOptions.LogLevel == PylanceSettings.LogLevel.Warning) {
                _logLevelCombo.SelectedIndex = 1;
            } else if (pyService.AnalysisOptions.LogLevel == PylanceSettings.LogLevel.Information) {
                _logLevelCombo.SelectedIndex = 2;
            } else if (pyService.AnalysisOptions.LogLevel == PylanceSettings.LogLevel.Trace) {
                _logLevelCombo.SelectedIndex = 3;
            } else {
                _logLevelCombo.SelectedIndex = 2;
            }
        }

        internal void SyncPageWithControlSettings(PythonToolsService pyService) {
            pyService.AnalysisOptions.StubPath = _stubsPath.Text;

            var pathsSeparators = new[] { ';', '\n', '\r' };
            pyService.AnalysisOptions.ExtraPaths = _searchPaths.Text?
                .Split(pathsSeparators, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            pyService.AnalysisOptions.TypeshedPaths = _typeshedPaths.Text?
                .Split(pathsSeparators, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

            pyService.AnalysisOptions.AutoSearchPaths = _autoSearchPath.Checked;
            pyService.AnalysisOptions.DiagnosticMode = _diagnosticsModeCombo.SelectedIndex == 0 ?
                PylanceSettings.DiagnosticMode.OpenFilesOnly : PylanceSettings.DiagnosticMode.Workspace;
            pyService.AnalysisOptions.DiagnosticMode = _diagnosticsModeCombo.SelectedIndex == 0 ?
                PylanceSettings.TypeCheckingMode.Basic : PylanceSettings.TypeCheckingMode.Strict;

            switch (_logLevelCombo.SelectedIndex) {
                case 0:
                    pyService.AnalysisOptions.LogLevel = PylanceSettings.LogLevel.Error;
                    break;
                case 1:
                    pyService.AnalysisOptions.LogLevel = PylanceSettings.LogLevel.Warning;
                    break;
                case 2:
                    pyService.AnalysisOptions.LogLevel = PylanceSettings.LogLevel.Information;
                    break;
                case 3:
                    pyService.AnalysisOptions.LogLevel = PylanceSettings.LogLevel.Trace;
                    break;
            }
        }
    }
}
