// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if WINDOWS

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Windows.Win32.System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Porta.Pty.Windows;

/// <summary>
///     Provides a pty connection for windows machines using PseudoConsole.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal class PtyProvider : IPtyProvider
{
    /// <inheritdoc />
    public Task<IPtyConnection> StartTerminalAsync(
        PtyOptions options,
        TraceSource trace,
        CancellationToken cancellationToken)
    {
        // check os version
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException(
                "PseudoConsole (ConPTY) is not supported on this version of Windows. " +
                "Windows 10 version 1809 (October 2018 Update) or later is required.");
        }

        return Task.FromResult<IPtyConnection>(StartPseudoConsole(options, trace));
    }

    private static string GetAppOnPath(string app, string cwd, IDictionary<string, string> env)
    {
        var isWow64 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null;
        var windir = Environment.GetEnvironmentVariable("WINDIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysnativePath = Path.Combine(windir, "Sysnative");
        var sysnativePathWithSlash = sysnativePath + Path.DirectorySeparatorChar;
        var system32Path = Path.Combine(windir, "System32");
        var system32PathWithSlash = system32Path + Path.DirectorySeparatorChar;

        try
        {
            // If we have an absolute path then we take it.
            if (Path.IsPathRooted(app))
            {
                if (isWow64)
                {
                    // If path is on system32, check sysnative first
                    if (app.StartsWith(system32PathWithSlash, StringComparison.OrdinalIgnoreCase))
                    {
                        var sysnativeApp = Path.Combine(sysnativePath, app[system32PathWithSlash.Length..]);
                        if (File.Exists(sysnativeApp))
                        {
                            return sysnativeApp;
                        }
                    }
                }
                else if (app.StartsWith(sysnativePathWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    // Change Sysnative to System32 if the OS is Windows but NOT WoW64. It's
                    // safe to assume that this was used by accident as Sysnative does not
                    // exist and will break in non-WoW64 environments.
                    return Path.Combine(system32Path, app.Substring(sysnativePathWithSlash.Length));
                }

                return app;
            }

            if (Path.GetDirectoryName(app) != string.Empty)
            {
                // We have a directory and the directory is relative. Make the path absolute
                // to the current working directory.
                return Path.Combine(cwd, app);
            }
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"Invalid terminal app path '{app}'");
        }
        catch (PathTooLongException)
        {
            throw new ArgumentException($"Terminal app path '{app}' is too long");
        }

        var pathEnvironment = (env.TryGetValue("PATH", out var p) ? p : null) ?? Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            // No PATH environment. Make path absolute to the cwd
            return Path.Combine(cwd, app);
        }

        var paths = new List<string>(pathEnvironment.Split(';', StringSplitOptions.RemoveEmptyEntries));
        if (isWow64)
        {
            // On Wow64, if %PATH% contains %WINDIR%\System32 but does not have %WINDIR%\Sysnative, add it before System32.
            var indexOfSystem32 = paths.FindIndex(entry =>
                string.Equals(entry, system32Path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry, system32PathWithSlash, StringComparison.OrdinalIgnoreCase));

            var indexOfSysnative = paths.FindIndex(entry =>
                string.Equals(entry, sysnativePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry, sysnativePathWithSlash, StringComparison.OrdinalIgnoreCase));

            if (indexOfSystem32 >= 0 && indexOfSysnative == -1)
            {
                paths.Insert(indexOfSystem32, sysnativePath);
            }
        }

        // We have a simple file name. We get the path variable from the env
        // and try to find the executable on the path.
        foreach (var pathEntry in paths)
        {
            bool isPathEntryRooted;
            try
            {
                isPathEntryRooted = Path.IsPathRooted(pathEntry);
            }
            catch (ArgumentException)
            {
                // Ignore invalid entry on %PATH%
                continue;
            }

            // The path entry is absolute.
            var fullPath = isPathEntryRooted ? Path.Combine(pathEntry, app) : Path.Combine(cwd, pathEntry, app);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            var withExtension = fullPath + ".com";
            if (File.Exists(withExtension))
            {
                return withExtension;
            }

            withExtension = fullPath + ".exe";
            if (File.Exists(withExtension))
            {
                return withExtension;
            }
        }

        // Not found on PATH. Make path absolute to the cwd
        return Path.Combine(cwd, app);
    }

    private static string GetEnvironmentString(IDictionary<string, string> environment)
    {
        var keys = new string[environment.Count];
        environment.Keys.CopyTo(keys, 0);

        var values = new string[environment.Count];
        environment.Values.CopyTo(values, 0);

        // Sort both by the keys
        // Windows 2000 requires the environment block to be sorted by the key.
        Array.Sort(keys, values, StringComparer.OrdinalIgnoreCase);

        // Create a list of null terminated "key=val" strings
        var result = new StringBuilder();
        for (var i = 0; i < environment.Count; ++i)
        {
            result.Append(keys[i]);
            result.Append('=');
            result.Append(values[i]);
            result.Append('\0');
        }

        // An extra null at the end indicates end of list.
        result.Append('\0');

        return result.ToString();
    }

    private unsafe static PseudoConsoleConnection StartPseudoConsole(PtyOptions options, TraceSource trace)
    {
        // Create a Job Object to ensure child processes are killed when the terminal exits.
        // This prevents zombie ConPTY sessions by using JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
        var jobObjectHandle = JobObject.Create();

        try
        {
            // Create the in/out pipes using Vanara
            if (!PInvoke.CreatePipe(out var inPipePseudoConsoleSide, out var inPipeOurSide, null, 0))
            {
                throw new InvalidOperationException("Could not create an anonymous pipe", new Win32Exception());
            }

            if (!PInvoke.CreatePipe(out var outPipeOurSide, out var outPipePseudoConsoleSide, null, 0))
            {
                throw new InvalidOperationException("Could not create an anonymous pipe", new Win32Exception());
            }

            // Create the Pseudo Console, using the pipes
            var hr = NativeMethods.CreatePseudoConsole(
                new COORD { X = (short)options.Cols, Y = (short)options.Rows },
                new SafeFileHandle(inPipePseudoConsoleSide.DangerousGetHandle(), false),
                new SafeFileHandle(outPipePseudoConsoleSide.DangerousGetHandle(), false),
                0,
                out var pseudoConsoleHandle);

            if (hr.Failed)
            {
                throw new InvalidOperationException($"Could not create pseudo console: {hr}", new Win32Exception(hr));
            }

            // IMPORTANT: Close the pseudoconsole side of the pipes after CreatePseudoConsole
            // The pseudoconsole now owns these handles, and keeping them open on our side
            // can cause input/output buffering issues.
            inPipePseudoConsoleSide.Dispose();
            outPipePseudoConsoleSide.Dispose();

            // Prepare the StartupInfoEx structure attached to the ConPTY.
            var startupInfo = new STARTUPINFOEXW();
            startupInfo.InitAttributeListAttachedToConPTY(pseudoConsoleHandle);

            try
            {
                var app = GetAppOnPath(options.App, options.Cwd, options.Environment);
                var arguments = options.VerbatimCommandLine ?
                    WindowsArguments.FormatVerbatim(options.CommandLine) :
                    WindowsArguments.Format(options.CommandLine);

                var commandLine = new StringBuilder(app.Length + arguments.Length + 4);
                var quoteApp = app.Contains(' ') && !app.StartsWith('"') && !app.EndsWith('"');
                if (quoteApp)
                {
                    commandLine.Append('"').Append(app).Append('"');
                }
                else
                {
                    commandLine.Append(app);
                }

                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    commandLine.Append(' ').Append(arguments);
                }

                trace.TraceInformation($"Starting terminal process '{app}' with command line {commandLine}");

                SafeProcessHandle? processHandle = null;
                SafeProcessHandle? mainThreadHandle = null;
                var pid = 0;
                bool success;

                // Build the environment block from the options
                var environmentBlock = GetEnvironmentString(options.Environment);
                // Pin the environment string and get a pointer to it
                var environmentHandle = GCHandle.Alloc(Encoding.Unicode.GetBytes(environmentBlock), GCHandleType.Pinned);
                try
                {
                    var processInfoRaw = new PROCESS_INFORMATION();
                    fixed (char* pCommandLine = commandLine.ToString())
                    fixed (char* pCwd = options.Cwd)
                    {
                        success = PInvoke.CreateProcess(
                            default, // lpApplicationName
                            new PWSTR(pCommandLine),
                            null, // lpProcessAttributes
                            null, // lpThreadAttributes
                            false, // bInheritHandles VERY IMPORTANT that this is false
                            PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                            (void*)environmentHandle.AddrOfPinnedObject(), // lpEnvironment - pass the environment block
                            new PCWSTR(pCwd),
                            (STARTUPINFOW*)&startupInfo,
                            &processInfoRaw);
                    }

                    if (success)
                    {
                        // Create Vanara safe handles from raw handles
                        processHandle = new SafeProcessHandle(processInfoRaw.hProcess, true);
                        mainThreadHandle = new SafeProcessHandle(processInfoRaw.hThread, true);
                        pid = (int)processInfoRaw.dwProcessId;

                        // Assign the process to the job object immediately after creation.
                        // This ensures the process and any children it spawns will be terminated
                        // when the job handle is closed (e.g., when our terminal crashes).
                        JobObject.AssignProcess(jobObjectHandle, processInfoRaw.hProcess);
                    }
                }
                finally
                {
                    environmentHandle.Free();
                }

                if (!success || processHandle is null || processHandle.IsInvalid || mainThreadHandle is null || mainThreadHandle.IsInvalid)
                {
                    var errorCode = Marshal.GetLastWin32Error();
                    var exception = new Win32Exception(errorCode);
                    throw new InvalidOperationException($"Could not start terminal process {commandLine}: {exception.Message}", exception);
                }

                var connectionOptions = new PseudoConsoleConnection.PseudoConsoleConnectionHandles(
                    inPipeOurSide,
                    outPipeOurSide,
                    pseudoConsoleHandle,
                    processHandle,
                    pid,
                    mainThreadHandle,
                    jobObjectHandle);

                return new PseudoConsoleConnection(connectionOptions);
            }
            finally
            {
                startupInfo.FreeAttributeList();
            }
        }
        catch
        {
            // If anything fails, make sure to dispose the job object
            jobObjectHandle.Dispose();
            throw;
        }
    }
}

#endif