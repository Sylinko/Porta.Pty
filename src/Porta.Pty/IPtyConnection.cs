// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty;

/// <summary>
///     Connection to a running pseudoterminal process.
/// </summary>
public interface IPtyConnection : IDisposable
{
    /// <summary>
    ///     Gets the stream for reading data from the pty.
    /// </summary>
    Stream ReaderStream { get; }

    /// <summary>
    ///     Gets the stream for writing data to the pty.
    /// </summary>
    Stream WriterStream { get; }

    /// <summary>
    ///     Gets the pty process ID.
    /// </summary>
    int Pid { get; }

    /// <summary>
    ///     Gets the pty process exit code.
    /// </summary>
    int ExitCode { get; }

    /// <summary>
    ///     Event fired when the pty process exits.
    /// </summary>
    event EventHandler<PtyExitedEventArgs>? ProcessExited;

    /// <summary>
    ///     Wait for the pty process to exit up to a given timeout.
    /// </summary>
    /// <param name="milliseconds">Timeout to wait for the process to exit.</param>
    /// <returns>True if the process exists within the timeout, false otherwise.</returns>
    bool WaitForExit(int milliseconds);

    /// <summary>
    /// Observe the root process even after it exits. Cancelling the wait does not kill the process.
    /// Native implementations can distinguish signals from successful observation of an exit code.
    /// </summary>
    async Task<PtyTermination> WaitForTerminationAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExit(object? sender, PtyExitedEventArgs args) => completion.TrySetResult(args.ExitCode);
        ProcessExited += OnExit;
        try
        {
            var code = WaitForExit(0) ? ExitCode : await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new PtyTermination(PtyTerminationKind.Exited, code);
        }
        finally
        {
            ProcessExited -= OnExit;
        }
    }

    /// <summary>
    ///     Immediately terminates the pty process.
    /// </summary>
    void Kill();

    /// <summary>
    ///     Change the size of the pty.
    /// </summary>
    /// <param name="cols">The number of columns.</param>
    /// <param name="rows">The number of rows.</param>
    void Resize(int cols, int rows);
}