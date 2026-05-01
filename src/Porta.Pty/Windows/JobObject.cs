// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if WINDOWS

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;
using Microsoft.Win32.SafeHandles;

namespace Porta.Pty.Windows;

/// <summary>
///     Provides Job Object functionality to ensure child processes are terminated
///     when the parent process exits, preventing zombie ConPTY sessions.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static class JobObject
{
    /// <summary>
    ///     Creates a Job Object configured to kill all assigned processes when the job handle is closed.
    ///     This ensures that if the terminal process crashes or exits unexpectedly, all child processes
    ///     (including conhost.exe and any PTY-backed console apps) are automatically terminated.
    /// </summary>
    /// <returns>A safe handle to the created job object.</returns>
    public unsafe static SafeFileHandle Create()
    {
        // Create an anonymous job object
        var jobHandle = PInvoke.CreateJobObject();
        if (jobHandle.IsInvalid)
        {
            throw new InvalidOperationException("Failed to create job object", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        // Configure the job to kill all processes when the job handle is closed
        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var pExtendedInfo = new ReadOnlySpan<byte>(&extendedInfo, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        if (!PInvoke.SetInformationJobObject(jobHandle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, pExtendedInfo))
        {
            jobHandle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set job object");
        }

        return jobHandle;
    }

    /// <summary>
    ///     Assigns a process to a job object.
    /// </summary>
    /// <param name="jobHandle">The job object handle.</param>
    /// <param name="processHandle">The process handle to assign.</param>
    public static void AssignProcess(SafeFileHandle jobHandle, HANDLE processHandle)
    {
        if (jobHandle == null || jobHandle.IsInvalid || jobHandle.IsClosed)
        {
            throw new ArgumentException("Invalid job object handle", nameof(jobHandle));
        }

        if (processHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid process handle", nameof(processHandle));
        }

        if (!PInvoke.AssignProcessToJobObject((HANDLE)jobHandle.DangerousGetHandle(), processHandle))
        {
            throw new InvalidOperationException("Failed to assign process to job object", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }
}

#endif