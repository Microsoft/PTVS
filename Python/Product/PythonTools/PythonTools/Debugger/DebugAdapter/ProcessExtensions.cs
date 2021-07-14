using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PythonTools.Debugger.DebugAdapter {
    static class ProcessExtensions {

        /// <summary>
        /// Asynchronously wait for the process to exit. If the wait is canceled or the timeout is reached
        /// the process will be stopped.
        /// </summary>
        /// <param name="process">The process to wait on.</param>
        /// <param name="cancellationToken">A cancellation token that allows the user to cancel invocation of the action.</param>
        /// <returns>true if the process exited, false otherwise.</returns>
        public static async Task<bool> WaitForExitAsync(this Process process, CancellationToken cancellationToken) {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void ExitAction(object sender, EventArgs args) => tcs.TrySetResult(true);
            process.EnableRaisingEvents = true;
            process.Exited += ExitAction;

            try {
                if (process.HasExited) {
                    return true;
                }

                if (await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cancellationToken)) != tcs.Task) {
                    process.Exited -= ExitAction;
                    process.Kill();

                    if (cancellationToken.IsCancellationRequested) {
                        tcs.TrySetCanceled();
                    } else {
                        // Timeout is reached so set the result to false.
                        tcs.TrySetResult(false);
                    }
                }

                // Re-await the task so that any exceptions/cancellation are rethrown.
                return await tcs.Task;
            } finally {
                process.Exited -= ExitAction;
            }
        }
    }
}
