// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if UNIX

using Microsoft.Win32.SafeHandles;

namespace Porta.Pty.Unix;

/// <summary>
///     A stream connected to a pty.
/// </summary>
internal sealed class PtyStream : FileStream
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="PtyStream" /> class.
    /// </summary>
    /// <param name="fd">The fd to connect the stream to.</param>
    /// <param name="fileAccess">The access permissions to set on the fd.</param>
    public PtyStream(int fd, FileAccess fileAccess)
        : base(new SafeFileHandle(fd, false), fileAccess, 1024, false)
    {
    }

    /// <inheritdoc />
    public override bool CanSeek => false;
}

#endif