// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if WINDOWS

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.System.Console;
using Microsoft.Win32.SafeHandles;

namespace Porta.Pty.Windows;

/// <summary>
///     A connection to a pseudoterminal spawned by native windows APIs.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class PseudoConsoleConnection : IPtyConnection
{
    private readonly Lock _disposeLock = new();
    private readonly Process _process;

    private PseudoConsoleConnectionHandles? _handles;
    private bool _isDisposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PseudoConsoleConnection" /> class.
    /// </summary>
    /// <param name="handles">The set of handles associated with the pseudoconsole.</param>
    public PseudoConsoleConnection(PseudoConsoleConnectionHandles handles)
    {
        // Use FileStream with the pipe handles for direct access
        // This avoids the buffering issues that can occur with AnonymousPipeClientStream
        ReaderStream = new FileStream(
            new SafeFileHandle(handles.OutPipeOurSide.DangerousGetHandle(), false),
            FileAccess.Read,
            0, // No buffering
            false);

        WriterStream = new FileStream(
            new SafeFileHandle(handles.InPipeOurSide.DangerousGetHandle(), false),
            FileAccess.Write,
            0, // No buffering - writes go directly to pipe
            false);

        _handles = handles;
        Pid = handles.Pid;
        _process = Process.GetProcessById(Pid);
        _process.Exited += HandleProcessExited;
        _process.EnableRaisingEvents = true;
    }

    /// <inheritdoc />
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;

    /// <inheritdoc />
    public Stream ReaderStream { get; }

    /// <inheritdoc />
    public Stream WriterStream { get; }

    /// <inheritdoc />
    public int Pid { get; }

    /// <inheritdoc />
    public int ExitCode => _process.ExitCode;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        // Unsubscribe from events first to prevent callbacks during disposal
        _process.Exited -= HandleProcessExited;

        // ConPTY cleanup order (per Microsoft documentation):
        // 1. Close the PseudoConsole handle - signals conhost to shut down
        // 2. Close the pipes - allows pending I/O to complete
        // 3. Close process/thread handles
        // 4. Close job object last - terminates any remaining processes

        if (_handles != null)
        {
            // Step 1: Close the pseudo console first (calls ClosePseudoConsole)
            // This signals conhost.exe to shut down gracefully
            _handles.PseudoConsoleHandle.Dispose();

            // Step 2: Close the pipes
            // Close our side of the pipes - this will cause any pending reads to complete
            _handles.InPipeOurSide.Dispose();
            _handles.OutPipeOurSide.Dispose();

            // Step 3: Close process and thread handles
            _handles.MainThreadHandle.Dispose();
            _handles.ProcessHandle.Dispose();

            // Step 4: Dispose the job object last - this will terminate any remaining
            // child processes due to JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            _handles.JobObjectHandle.Dispose();

            _handles = null;
        }

        // Dispose streams (they don't own the underlying handles)
        ReaderStream.Dispose();
        WriterStream.Dispose();

        // Dispose the Process object
        _process.Dispose();
    }

    /// <inheritdoc />
    public void Kill()
    {
        _process.Kill();
    }

    /// <inheritdoc />
    public void Resize(int cols, int rows)
    {
        var handles = _handles;
        ObjectDisposedException.ThrowIf(handles is null || _isDisposed, nameof(PseudoConsoleConnection));

        var coord = new COORD { X = (short)cols, Y = (short)rows };
        var hr = PInvoke.ResizePseudoConsole(handles.PseudoConsoleHandle, coord);
        if (hr.Failed)
        {
            throw new InvalidOperationException($"Could not resize pseudo console: {hr}", new Win32Exception(hr));
        }
    }

    /// <inheritdoc />
    public bool WaitForExit(int milliseconds)
    {
        return _process.WaitForExit(milliseconds);
    }

    private void HandleProcessExited(object? sender, EventArgs e)
    {
        // Check if we're disposed to avoid raising events during/after disposal
        if (_isDisposed)
        {
            return;
        }

        ProcessExited?.Invoke(this, new PtyExitedEventArgs(_process.ExitCode));
    }

    /// <summary>
    ///     handles to resources creates when a pseudoconsole is spawned.
    /// </summary>
    public sealed class PseudoConsoleConnectionHandles
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="PseudoConsoleConnectionHandles" /> class.
        /// </summary>
        /// <param name="inPipeOurSide">the input pipe on the local side (we write to this).</param>
        /// <param name="outPipeOurSide">the output pipe on the local side (we read from this).</param>
        /// <param name="pseudoConsoleHandle">the handle to the pseudoconsole.</param>
        /// <param name="processHandle">the handle to the spawned process.</param>
        /// <param name="pid">the process ID.</param>
        /// <param name="mainThreadHandle">the handle to the main thread.</param>
        /// <param name="jobObjectHandle">the handle to the job object that manages process lifetime.</param>
        public PseudoConsoleConnectionHandles(
            SafeHandle inPipeOurSide,
            SafeHandle outPipeOurSide,
            SafeHandle pseudoConsoleHandle,
            SafeHandle processHandle,
            int pid,
            SafeHandle mainThreadHandle,
            SafeHandle jobObjectHandle)
        {
            InPipeOurSide = inPipeOurSide;
            OutPipeOurSide = outPipeOurSide;
            PseudoConsoleHandle = pseudoConsoleHandle;
            ProcessHandle = processHandle;
            Pid = pid;
            MainThreadHandle = mainThreadHandle;
            JobObjectHandle = jobObjectHandle;
        }

        /// <summary>
        ///     Gets the input pipe on the local side (we write to this to send to console).
        /// </summary>
        internal SafeHandle InPipeOurSide { get; }

        /// <summary>
        ///     Gets the output pipe on the local side (we read from this to get console output).
        /// </summary>
        internal SafeHandle OutPipeOurSide { get; }

        /// <summary>
        ///     Gets the handle to the pseudoconsole.
        /// </summary>
        internal SafeHandle PseudoConsoleHandle { get; }

        /// <summary>
        ///     Gets the handle to the spawned process.
        /// </summary>
        internal SafeHandle ProcessHandle { get; }

        /// <summary>
        ///     Gets the process ID.
        /// </summary>
        internal int Pid { get; }

        /// <summary>
        ///     Gets the handle to the main thread.
        /// </summary>
        internal SafeHandle MainThreadHandle { get; }

        /// <summary>
        ///     Gets the handle to the job object that manages process lifetime.
        ///     When this handle is closed, all processes assigned to the job are terminated.
        /// </summary>
        internal SafeHandle JobObjectHandle { get; }
    }
}

#endif