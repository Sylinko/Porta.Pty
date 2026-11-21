// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if UNIX

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Porta.Pty.Unix;

/// <summary>
///     A connection to a Unix-style pseudoterminal.
/// </summary>
internal abstract class PtyConnection : IPtyConnection
{
    private const int EINTR = 4;
    private const int ESRCH = 3;

    private readonly int controller;
    private readonly SafeFileHandle controllerOwner;
    private readonly TaskCompletionSource<PtyTermination> termination = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool isDisposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PtyConnection" /> class.
    /// </summary>
    /// <param name="controller">The fd of the pty controller.</param>
    /// <param name="pid">The id of the spawned process.</param>
    public PtyConnection(int controller, int pid)
    {
        controllerOwner = new SafeFileHandle(controller, ownsHandle: true);
        ReaderStream = new PtyStream(controller, FileAccess.Read);
        WriterStream = new PtyStream(controller, FileAccess.Write);

        this.controller = controller;
        this.Pid = pid;
        var childWatcherThread = new Thread(ChildWatcherThreadProc)
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = $"Watcher thread for child process {pid}",
        };

        childWatcherThread.Start();
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
    public int ExitCode { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;

        ReaderStream?.Dispose();
        WriterStream?.Dispose();

        // Try to kill the process, but don't throw if it already exited
        if (!termination.Task.IsCompleted)
        {
            TryKill();
        }
        controllerOwner.Dispose();
    }

    /// <inheritdoc />
    public void Kill()
    {
        if (!Kill(controller))
        {
            var errno = Marshal.GetLastWin32Error();
            // ESRCH means the process doesn't exist (already exited) - that's OK
            if (errno != ESRCH)
            {
                throw new InvalidOperationException($"Killing terminal failed with error {errno}");
            }
        }
    }

    /// <inheritdoc />
    public void Resize(int cols, int rows)
    {
        if (!Resize(controller, cols, rows))
        {
            throw new InvalidOperationException($"Resizing terminal failed with error {Marshal.GetLastWin32Error()}");
        }
    }

    /// <inheritdoc />
    public bool WaitForExit(int milliseconds)
    {
        return termination.Task.Wait(milliseconds);
    }

    /// <summary>
    ///     OS-specific implementation of the pty-resize function.
    /// </summary>
    /// <param name="controller">The fd of the pty controller.</param>
    /// <param name="cols">The number of columns to resize to.</param>
    /// <param name="rows">The number of rows to resize to.</param>
    /// <returns>True if the function suceeded to resize the pty, false otherwise.</returns>
    protected abstract bool Resize(int controller, int cols, int rows);

    /// <summary>
    ///     Kills the terminal process.
    /// </summary>
    /// <param name="controller">The fd of the pty controller.</param>
    /// <returns>True if the function succeeded in killing the process, false otherwise.</returns>
    protected abstract bool Kill(int controller);

    /// <summary>
    ///     OS-specific implementation of waiting on the given process id.
    /// </summary>
    /// <param name="pid">The process id to wait on.</param>
    /// <param name="status">The status of the process.</param>
    /// <returns>True if the function succeeded to get the status of the process, false otherwise.</returns>
    protected abstract bool WaitPid(int pid, ref int status);

    /// <summary>
    ///     Attempts to kill the process without throwing an exception.
    /// </summary>
    private void TryKill()
    {
        try
        {
            Kill();
        }
        catch
        {
            // Ignore errors during cleanup - process may have already exited
        }
    }

    /// <inheritdoc />
    public Task<PtyTermination> WaitForTerminationAsync(CancellationToken cancellationToken = default) =>
        termination.Task.WaitAsync(cancellationToken);

    private void ChildWatcherThreadProc()
    {
        var status = 0;
        PtyTermination result;
        while (true)
        {
            if (WaitPid(Pid, ref status))
            {
                // waitpid(options: 0) on Linux and Darwin. Traced stops are not termination.
                var signal = status & 0x7f;
                if (signal == 0x7f) continue;
                result = signal == 0
                    ? new PtyTermination(PtyTerminationKind.Exited, (status >> 8) & 0xff)
                    : new PtyTermination(PtyTerminationKind.Signaled, signal);
                break;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == EINTR) continue;
            result = new PtyTermination(PtyTerminationKind.ObservationFailed, error);
            break;
        }

        ExitCode = result.Kind switch
        {
            PtyTerminationKind.Exited => result.Value,
            PtyTerminationKind.Signaled => 128 + result.Value,
            _ => -1
        };
        termination.SetResult(result);
        ProcessExited?.Invoke(this, new PtyExitedEventArgs(ExitCode));
    }
}

#endif
