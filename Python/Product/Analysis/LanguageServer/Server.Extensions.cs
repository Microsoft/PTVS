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
using System.Threading.Tasks;

namespace Microsoft.PythonTools.Analysis.LanguageServer {
    partial class Server {
        public async Task LoadExtension(PythonAnalysisExtensionParams extension) {
            var ext = ActivateObject<ILanguageServerExtension>(extension.assembly, extension.typeName, extension.properties);
            if (ext != null) {
                var n = ext.Name;
                ext.Register(this);
                if (!string.IsNullOrEmpty(n)) {
                    _extensions.AddOrUpdate(n, ext, (_, previous) => {
                        (previous as IDisposable)?.Dispose();
                        return ext;
                    });
                }
            }
        }

        public async Task<ExtensionCommandResult> ExtensionCommand(ExtensionCommandParams @params) {
            if (string.IsNullOrEmpty(@params.extensionName)) {
                throw new ArgumentNullException(nameof(@params.extensionName));
            }

            if (!_extensions.TryGetValue(@params.extensionName, out var ext)) {
                throw new LanguageServerException(LanguageServerException.UnknownExtension, "No extension loaded with name: " + @params.extensionName);
            }

            return new ExtensionCommandResult {
                properties = ext?.ExecuteCommand(@params.command, @params.properties)
            };
        }

    }
}
