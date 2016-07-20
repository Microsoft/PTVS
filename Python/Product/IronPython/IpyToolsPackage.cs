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
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.IronPythonTools.Interpreter;
using Microsoft.PythonTools;
using Microsoft.VisualStudio.Shell;

namespace Microsoft.IronPythonTools {
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [Guid(IpyToolsPackageGuid)]
    [Description("Python Tools IronPython Interpreter")]
    class IpyToolsPackage : Package {
        public const string IpyToolsPackageGuid = "af7eaf4b-5af3-3622-b39a-7ae7ed25e7b2";

        public IpyToolsPackage() {
        }
    }
}
