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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.DsTools.Core.Disposables;
using Microsoft.PythonTools.Analysis.Infrastructure;

namespace Microsoft.Python.LanguageServer {
    internal sealed class PathsWatcher : IDisposable {
        private readonly DisposableBag _disposableBag = new DisposableBag(nameof(PathsWatcher));
        private SingleThreadSynchronizationContext _syncContext = new SingleThreadSynchronizationContext();
        private readonly Action _onChanged;
        private readonly object _lock = new object();

        private Timer _throttleTimer;
        private bool _changedSinceLastTick;

        public PathsWatcher(string[] paths, Action onChanged) {
            if (paths?.Length == 0) {
                return;
            }

            _onChanged = onChanged;

            var list = new List<FileSystemWatcher>();
            var reduced = ReduceToCommonRoots(paths);
            foreach (var p in reduced) {
                var fsw = new FileSystemWatcher(p);
                fsw.IncludeSubdirectories = true;
                fsw.EnableRaisingEvents = true;

                fsw.Changed += OnChanged;
                fsw.Created += OnChanged;
                fsw.Deleted += OnChanged;

                _disposableBag
                    .Add(() => fsw.Changed -= OnChanged)
                    .Add(() => fsw.Created -= OnChanged)
                    .Add(() => fsw.Deleted -= OnChanged)
                    .Add(fsw);
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e) {
            // Throttle calls so we don't get flooded with requests
            // if there is massive change to the file structure.
            lock (_lock) {
                _changedSinceLastTick = true;
                _throttleTimer = _throttleTimer ?? new Timer(TimerProc, null, 500, 500);
            }
        }

        private void TimerProc(object o) {
            lock (_lock) {
                if (!_changedSinceLastTick && _throttleTimer != null) {
                    _syncContext.Post(s => _onChanged(), null);
                    _throttleTimer.Dispose();
                    _throttleTimer = null;
                }
                _changedSinceLastTick = false;
            }
        }
        public void Dispose() => _disposableBag.TryDispose();

        private IEnumerable<string> ReduceToCommonRoots(string[] paths) {
            if (paths.Length == 0) {
                return paths;
            }

            var original = paths.OrderBy(s => s.Length).ToList();
            List<string> reduced = null;

            while (reduced == null || original.Count > reduced.Count) {
                var shortest = original[0];
                reduced = new List<string>();
                reduced.Add(shortest);
                for (var i = 1; i < original.Count; i++) {
                    // take all that do not start with the shortest
                    if (!original[i].StartsWith(shortest)) {
                        reduced.Add(original[i]);
                    }
                }
                original = reduced;
            }
            return reduced;
        }
    }
}
