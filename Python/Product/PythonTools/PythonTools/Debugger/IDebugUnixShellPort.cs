using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.PythonTools.Debugger {

    /// <summary>
    ///  Copy of internal IDebugUnixShellPort - allows for querying if a port is for a remote machine or not
    /// </summary>
    [ComImport()]
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("808CC6CA-45B1-47D5-9779-62BAA597BA50")]
    public interface IDebugUnixShellPort {
        /// <summary>
        /// Synchronously executes the specified shell command and returns the output and exit code
        /// of the command.
        /// </summary>
        /// <param name="commandDescription">Description of the command to use in a wait 
        /// dialog if command takes a long time to execute</param>
        /// <param name="commandText">Command line to execute on the remote system</param>
        /// <param name="commandOutput">Stdout/err which the command writes</param>
        /// <param name="timeout">timeout before the command should be aborted</param>
        /// <param name="exitCode">exit code of the command</param>
        void ExecuteSyncCommand(string commandDescription, string commandText, out string commandOutput, int timeout, out int exitCode);

        /// <summary>
        /// Starts the execution of the specified command, using call back interfaces to 
        /// receive its output, and using a command interface to send it input or abort 
        /// it.
        /// </summary>
        /// <param name="commandText">Text of the command to execut</param>
        /// <param name="callback">Callback which will receive the output and events 
        /// from the command</param>
        /// <param name="asyncCommand">Returned command object</param>
        void BeginExecuteAsyncCommand(string commandText, object callback, out object asyncCommand);

        /// <summary>
        /// Copy a single file from the local machine to the remote machine.
        /// </summary>
        /// <param name="sourcePath">File on the local machine.</param>
        /// <param name="destinationPath">Destination path on the remote machine.</param>
        void CopyFile(string sourcePath, string destinationPath);

        /// <summary>
        /// Creates directory provided the path. Does not fail if the directory already exists.
        /// </summary>
        /// <param name="path">Path on the remote machine.</param>
        /// <returns>Full path of the created directory.</returns>
        string MakeDirectory(string path);

        /// <summary>
        /// Gets the home directory of the user.
        /// </summary>
        /// <returns>Home directory of the user.</returns>
        string GetUserHomeDirectory();

        /// <returns>True if the remote machine is OSX.</returns>
        bool IsOSX();

        /// <returns>True if the remote machine is Linux.</returns>
        bool IsLinux();
    }
}
