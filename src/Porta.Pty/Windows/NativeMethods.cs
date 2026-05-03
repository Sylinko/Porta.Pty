// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if WINDOWS

using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
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

    /// <inheritdoc cref="CreatePseudoConsole(COORD, HANDLE, HANDLE, uint, HPCON*)"/>
    [OverloadResolutionPriority(1)]
    public static unsafe HRESULT CreatePseudoConsole(
        COORD size,
        SafeHandle? hInput,
        SafeHandle? hOutput,
        uint dwFlags,
        out ClosePseudoConsoleSafeHandle phPc)
    {
        ArgumentNullException.ThrowIfNull(hInput);
        ArgumentNullException.ThrowIfNull(hOutput);

        var hInputAddRef = false;
        var hOutputAddRef = false;

        try
        {
            hInput.DangerousAddRef(ref hInputAddRef);
            var hInputLocal = (HANDLE)hInput.DangerousGetHandle();

            hOutput.DangerousAddRef(ref hOutputAddRef);
            var hOutputLocal = (HANDLE)hOutput.DangerousGetHandle();

            HPCON phPcLocal;
            phPc = new ClosePseudoConsoleSafeHandle(0, ownsHandle: true);
            var result = CreatePseudoConsole(size, hInputLocal, hOutputLocal, dwFlags, &phPcLocal);
            Marshal.InitHandle(phPc, phPcLocal);
            return result;
        }
        finally
        {
            if (hInputAddRef) hInput.DangerousRelease();
            if (hOutputAddRef) hOutput.DangerousRelease();
        }
    }

    /// <summary>See reference information about the CreatePseudoConsole function, which allocates a new pseudoconsole for the calling process.</summary>
    /// <param name="size">The dimensions of the window/buffer in count of characters that will be used on initial creation of the pseudoconsole. This can be adjusted later with [ResizePseudoConsole](resizepseudoconsole.md).</param>
    /// <param name="hInput">An open handle to a stream of data that represents user input to the device. This is currently restricted to [synchronous](/windows/desktop/Sync/synchronization-and-overlapped-input-and-output) I/O.</param>
    /// <param name="hOutput">An open handle to a stream of data that represents application output from the device. This is currently restricted to [synchronous](/windows/desktop/Sync/synchronization-and-overlapped-input-and-output) I/O.</param>
    /// <param name="dwFlags">
    /// <para>The value can be one of the following: | Value | Meaning | |-|-| | **0** | Perform a standard pseudoconsole creation. | | **PSEUDOCONSOLE_INHERIT_CURSOR** (DWORD)1 | The created pseudoconsole session will attempt to inherit the cursor position of the parent console. |</para>
    /// <para><see href="https://learn.microsoft.com/windows/console/createpseudoconsole#parameters">Read more on learn.microsoft.com</see>.</para>
    /// </param>
    /// <param name="phPc">Pointer to a location that will receive a handle to the new pseudoconsole device.</param>
    /// <returns>
    /// <para>Type: **HRESULT** If this method succeeds, it returns **S_OK**. Otherwise, it returns an **HRESULT** error code.</para>
    /// </returns>
    /// <remarks>
    /// <para>This function is primarily used by applications attempting to be a terminal window for a command-line user interface (CUI) application. The callers become responsible for presentation of the information on the output stream and for collecting user input and serializing it into the input stream. The input and output streams encoded as UTF-8 contain plain text interleaved with [Virtual Terminal Sequences](console-virtual-terminal-sequences.md). On the output stream, the [virtual terminal sequences](console-virtual-terminal-sequences.md) can be decoded by the calling application to layout and present the plain text in a display window. On the input stream, plain text represents standard keyboard keys input by a user. More complicated operations are represented by encoding control keys and mouse movements as [virtual terminal sequences](console-virtual-terminal-sequences.md) embedded in this stream. The handle created by this function must be closed with [ClosePseudoConsole](closepseudoconsole.md) when operations are complete. If using `PSEUDOCONSOLE_INHERIT_CURSOR`, the calling application should be prepared to respond to the request for the cursor state in an asynchronous fashion on a background thread by forwarding or interpreting the request for cursor information that will be received on `hOutput` and replying on `hInput`. Failure to do so may cause the calling application to hang while making another request of the pseudoconsole system.</para>
    /// <para><see href="https://learn.microsoft.com/windows/console/createpseudoconsole#">Read more on learn.microsoft.com</see>.</para>
    /// </remarks>
    [DllImport("conpty.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern unsafe HRESULT CreatePseudoConsole(
        COORD size,
        HANDLE hInput,
        HANDLE hOutput,
        uint dwFlags,
        HPCON* phPc);

    /// <inheritdoc cref="ResizePseudoConsole(HPCON, COORD)"/>
    [OverloadResolutionPriority(1)]
    public static HRESULT ResizePseudoConsole(SafeHandle? hPc, COORD size)
    {
        ArgumentNullException.ThrowIfNull(hPc);

        var hPcAddRef = false;
        try
        {
            hPc.DangerousAddRef(ref hPcAddRef);
            var hPcLocal = (HPCON)hPc.DangerousGetHandle();
            return ResizePseudoConsole(hPcLocal, size);
        }
        finally
        {
            if (hPcAddRef) hPc.DangerousRelease();
        }
    }

    /// <summary>See reference information about the ResizePseudoConsole function, which resizes the internal buffers for a pseudoconsole to the given size.</summary>
    /// <param name="hPc">A handle to an active pseudoconsole as opened by [CreatePseudoConsole](createpseudoconsole.md).</param>
    /// <param name="size">The dimensions of the window/buffer in count of characters that will be used for the internal buffer of this pseudoconsole.</param>
    /// <returns>
    /// <para>Type: **HRESULT** If this method succeeds, it returns **S_OK**. Otherwise, it returns an **HRESULT** error code.</para>
    /// </returns>
    /// <remarks>This function can resize the internal buffers in the pseudoconsole session to match the window/buffer size being used for display on the terminal end. This ensures that attached Command-Line Interface (CUI) applications using the [Console Functions](console-functions.md) to communicate will have the correct dimensions returned in their calls.</remarks>
    [DllImport("conpty.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern HRESULT ResizePseudoConsole(HPCON hPc, COORD size);
}

/// <summary>
/// Represents a Win32 handle that can be closed with <see cref="PInvoke.ClosePseudoConsole(HPCON)"/>.
/// </summary>
[GeneratedCode("Microsoft.Windows.CsWin32", "0.3.269+368685089b.RR")]
internal class ClosePseudoConsoleSafeHandle : SafeHandle
{
    internal ClosePseudoConsoleSafeHandle(IntPtr preexistingHandle, bool ownsHandle = true) : base(new IntPtr(-1L), ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    public override bool IsInvalid => handle.ToInt64() == -1L || handle.ToInt64() == 0L;

    protected override bool ReleaseHandle()
    {
        ClosePseudoConsole((HPCON)handle);
        return true;
    }

    /// <summary>See reference information about the ClosePseudoConsole function, which closes a pseudoconsole from the given handle.</summary>
    /// <param name="hPc">A handle to an active pseudoconsole as opened by [CreatePseudoConsole](createpseudoconsole.md).</param>
    /// <returns>*none*</returns>
    /// <remarks>
    /// <para>Upon closing a pseudoconsole, client applications attached to the session will be terminated as well. A final painted frame may arrive on the `hOutput` handle originally provided to [CreatePseudoConsole](createpseudoconsole.md) when this API is called. It is expected that the caller will drain this information from the communication channel buffer and either present it or discard it. Failure to drain the buffer may cause the Close call to wait indefinitely until it is drained or the communication channels are broken another way.</para>
    /// <para><see href="https://learn.microsoft.com/windows/console/closepseudoconsole#">Read more on learn.microsoft.com</see>.</para>
    /// </remarks>
    [DllImport("conpty.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern void ClosePseudoConsole(HPCON hPc);
}

#endif