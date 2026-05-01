// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if WINDOWS

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Porta.Pty.Windows;

[SupportedOSPlatform("windows10.0.17763")]
internal static partial class NativeMethods
{
    // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE value
    // This is ProcThreadAttributePseudoConsole (22) | PROC_THREAD_ATTRIBUTE_INPUT (0x20000)
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016; // 22 | 0x20000

    // Extension method to initialize STARTUPINFOEX with PseudoConsole attribute
    extension(ref STARTUPINFOEXW startupInfo)
    {
        public unsafe void InitAttributeListAttachedToConPTY(SafeHandle pseudoConsoleHandle)
        {
            startupInfo.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
            startupInfo.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;

            const int AttributeCount = 1;
            nuint size = 0;

            // Create the appropriately sized thread attribute list
            bool wasInitialized = PInvoke.InitializeProcThreadAttributeList(default, AttributeCount, 0, &size);
            if (wasInitialized || size == 0)
            {
                throw new InvalidOperationException(
                    $"Couldn't get the size of the process attribute list for {AttributeCount} attributes",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            startupInfo.lpAttributeList = (LPPROC_THREAD_ATTRIBUTE_LIST)Marshal.AllocHGlobal((int)size);
            if (startupInfo.lpAttributeList == IntPtr.Zero)
            {
                throw new OutOfMemoryException("Couldn't reserve space for a new process attribute list");
            }

            // Set startup info's attribute list & initialize it
            wasInitialized = PInvoke.InitializeProcThreadAttributeList(startupInfo.lpAttributeList, AttributeCount, 0, &size);
            if (!wasInitialized)
            {
                throw new InvalidOperationException("Couldn't create new process attribute list", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            // Set thread attribute list's Pseudo Console to the specified ConPTY
            // Note: We use our own P/Invoke for UpdateProcThreadAttribute because:
            // 1. Vanara's PROC_THREAD_ATTRIBUTE enum doesn't include PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE (newer Win10 feature)
            // 2. Vanara's UpdateProcThreadAttribute doesn't accept IntPtr for custom attribute values
            wasInitialized = UpdateProcThreadAttributeCustom(
                startupInfo.lpAttributeList,
                0,
                new IntPtr(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE),
                pseudoConsoleHandle.DangerousGetHandle(),
                (nuint)sizeof(IntPtr),
                IntPtr.Zero,
                IntPtr.Zero);

            if (!wasInitialized)
            {
                throw new InvalidOperationException("Couldn't update process attribute list", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }

        public void FreeAttributeList()
        {
            if (startupInfo.lpAttributeList == IntPtr.Zero) return;

            PInvoke.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            startupInfo.lpAttributeList = default;
        }
    }

    /// <summary>
    ///     Custom P/Invoke for UpdateProcThreadAttribute.
    ///     Required because Vanara's version doesn't support PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
    ///     which is a newer Windows 10 feature not yet in Vanara's PROC_THREAD_ATTRIBUTE enum.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttributeCustom(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nuint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);
}

#endif