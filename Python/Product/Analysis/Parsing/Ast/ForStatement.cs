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

using System.Text;

namespace Microsoft.PythonTools.Parsing.Ast {
    public class ForStatement : Statement {
        private int? _keywordEndIndex;

        public ForStatement(Expression left, Expression list, Statement body, Statement else_) {
            Left = left;
            List = list;
            Body = body;
            Else = else_;
        }

        public ForStatement(Expression left, Expression list, Statement body, Statement else_, bool isAsync)
            : this(left, list, body, else_) {
            IsAsync = isAsync;
        }

        public int HeaderIndex { get; set; }
        public int ElseIndex { get; set; }
        internal void SetKeywordEndIndex(int index) => _keywordEndIndex = index;
        public override int KeywordEndIndex => _keywordEndIndex ?? (StartIndex + (IsAsync ? 9 : 3));
        public override int KeywordLength => KeywordEndIndex - StartIndex;

        public Expression Left { get; }
        public Statement Body { get; set; }
        public Expression List { get; set; }
        public Statement Else { get; }
        public bool IsAsync { get; }

        public override void Walk(PythonWalker walker) {
            if (walker.Walk(this)) {
                Left?.Walk(walker);
                List?.Walk(walker);
                Body?.Walk(walker);
                Else?.Walk(walker);
            }
            walker.PostWalk(this);
        }

        public int GetIndexOfFor(PythonAst ast) {
            if (!IsAsync) {
                return StartIndex;
            }
            return StartIndex + this.GetFourthWhiteSpace(ast).Length + 5;
        }

        public int GetIndexOfIn(PythonAst ast) {
            if (this.IsIncompleteNode(ast)) {
                return -1;
            }
            return Left.EndIndex + this.GetSecondWhiteSpace(ast).Length;
        }

        internal override void AppendCodeStringStmt(StringBuilder res, PythonAst ast, CodeFormattingOptions format) {
            format.ReflowComment(res, this.GetPreceedingWhiteSpace(ast));
            if (IsAsync) {
                res.Append("async");
                res.Append(this.GetFourthWhiteSpace(ast));
            }
            res.Append("for");
            Left.AppendCodeString(res, ast, format);
            if (!this.IsIncompleteNode(ast)) {
                res.Append(this.GetSecondWhiteSpace(ast));
                res.Append("in");
                List.AppendCodeString(res, ast, format);
                Body.AppendCodeString(res, ast, format);   // colon is handled by suite statements...
                if (Else != null) {
                    format.ReflowComment(res, this.GetThirdWhiteSpace(ast));
                    res.Append("else");
                    Else.AppendCodeString(res, ast, format);
                }
            }
        }
    }
}
