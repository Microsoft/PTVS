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

using System;
using Microsoft.PythonTools.Analysis;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Microsoft.PythonTools.Infrastructure {
    /// <summary>
    /// Provides access to the DesignerContext and WpfEventBindingProvider.
    /// </summary>
    public interface IXamlDesignerSupport {
        Guid DesignerContextTypeGuid { get; }
        object CreateDesignerContext();
        void InitializeEventBindingProvider(object designerContext, IXamlDesignerCallback callback);
    }

    public interface IXamlDesignerCallback {
        ITextView TextView {
            get;
        }
        ITextBuffer Buffer {
            get;
        }

        InsertionPoint GetInsertionPoint(string className);

        string[] FindMethods(string className, int? paramCount);

        MethodInformation GetMethodInfo(string className, string methodName);
    }

    public sealed class InsertionPoint {
        public readonly int Location, Indentation;
        public InsertionPoint(int location, int indentation) {
            Location = location;
            Indentation = indentation;
        }
    }

    public sealed class MethodInformation {
        public readonly bool IsFound;
        public readonly int Start, End;

        public MethodInformation(int start, int end, bool found) {
            Start = start;
            End = end;
            IsFound = found;
        }
    }

}
