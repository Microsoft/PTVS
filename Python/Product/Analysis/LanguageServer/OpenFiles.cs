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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.PythonTools.Intellisense;

namespace Microsoft.PythonTools.Analysis.LanguageServer {
    internal sealed class OpenFiles {
        private readonly ConcurrentDictionary<Uri, OpenFile> _files = new ConcurrentDictionary<Uri, OpenFile>();
        private readonly ILogger _log;
        private readonly ProjectFiles _projectFiles;

        public OpenFiles(ProjectFiles projectFiles, ILogger log) {
            _projectFiles = projectFiles;
            _log = log;
        }
        public OpenFile GetDocument(Uri uri) => _files.GetOrAdd(uri, _ => new OpenFile(_projectFiles, _log));
        public void Remove(Uri uri) {
            _files.TryRemove(uri, out var entry);
            entry?.Dispose();
        }
    }

    sealed class OpenFile: IDisposable {
        private readonly ILogger _log;
        private readonly ProjectFiles _projectFiles;
        private readonly ManualResetEventSlim _documentChangeProcessingComplete = new ManualResetEventSlim(true);
        private int _documentChangeReentrancyCount;
        private DelayedAction _delayedAction;

        public IDictionary<int, BufferVersion> LastReportedDiagnostics { get; } = new Dictionary<int, BufferVersion>();
        public List<DidChangeTextDocumentParams> PendingChanges { get; } = new List<DidChangeTextDocumentParams>();

        public OpenFile(ProjectFiles projectFiles, ILogger log) {
            _projectFiles = projectFiles;
            _log = log;
        }

        public void Dispose() => _delayedAction?.Dispose();

        public void WaitForChangeProcessingComplete(CancellationToken token) => _documentChangeProcessingComplete.Wait(token);

        public void DidChangeTextDocument(DidChangeTextDocumentParams @params, int analysisDelay, Action<IDocument> enqueueAction) {
            var changes = @params.contentChanges;
            if (changes == null) {
                return;
            }

            var uri = @params.textDocument.uri;
            var doc = _projectFiles.GetEntry(uri) as IDocument;
            if (doc == null) {
                return;
            }

            _delayedAction?.Dispose();
            _documentChangeProcessingComplete.Reset();
            _documentChangeReentrancyCount++;

            try {
                var part = _projectFiles.GetPart(uri);
                _log.TraceMessage($"Received changes for {uri}");

                var docVersion = Math.Max(doc.GetDocumentVersion(part), 0);
                var fromVersion = Math.Max(@params.textDocument.version - 1 ?? docVersion, 0);

                if (fromVersion > docVersion && @params.contentChanges?.Any(c => c.range == null) != true) {
                    // Expected from version hasn't been seen yet, and there are no resets in this
                    // change, so enqueue it for later.
                    _log.TraceMessage($"Deferring changes for {uri} until version {fromVersion} is seen");
                    lock (PendingChanges) {
                        PendingChanges.Add(@params);
                    }
                    return;
                }

                var toVersion = @params.textDocument.version ?? (fromVersion + changes.Length);

                doc.UpdateDocument(part, new DocumentChangeSet(
                    fromVersion,
                    toVersion,
                    changes.Select(c => new DocumentChange {
                        ReplacedSpan = c.range.GetValueOrDefault(),
                        WholeBuffer = !c.range.HasValue,
                        InsertedText = c.text
                    })
                ));

                DidChangeTextDocumentParams? next = null;
                lock (PendingChanges) {
                    var notExpired = PendingChanges
                        .Where(p => p.textDocument.version.GetValueOrDefault() >= toVersion)
                        .OrderBy(p => p.textDocument.version.GetValueOrDefault())
                        .ToArray();

                    PendingChanges.Clear();
                    if (notExpired.Any()) {
                        next = notExpired.First();
                        PendingChanges.AddRange(notExpired.Skip(1));
                    }
                }
                if (next.HasValue) {
                    DidChangeTextDocument(next.Value, analysisDelay, enqueueAction);
                }
            } finally {
                _documentChangeReentrancyCount--;
                if (_documentChangeReentrancyCount == 0) {

                    _log.TraceMessage($"Applied changes to {uri}");
                    if (analysisDelay > 0) {
                        _delayedAction = new DelayedAction(() => enqueueAction(doc), analysisDelay);
                    } else {
                        enqueueAction(doc);
                    }
                    _documentChangeProcessingComplete.Set();
                }
            }
        }
    }
}
