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

using Microsoft.CookiecutterTools.Infrastructure;

namespace Microsoft.CookiecutterTools.Commands {
    /// <summary>
    /// Provides the command for opening the cookiecutter window.
    /// </summary>
    class HomeCommand : Command {
        private readonly CookiecutterToolWindow _window;

        public HomeCommand(CookiecutterToolWindow window) {
            _window = window;
        }

        public override void DoCommand(object sender, EventArgs args) {
            _window.Home();
        }

        public override int CommandId {
            get { return (int)PackageIds.cmdidHome; }
        }
    }
}
