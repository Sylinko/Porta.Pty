// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;

#if WINDOWS
using System.Runtime.Versioning;
#endif

namespace Porta.Pty;

/// <summary>
///     Provides platform specific functionality.
/// </summary>
internal static class PlatformServices
{
#if WINDOWS
    private readonly static IDictionary<string, string> WindowsPtyEnvironment = new Dictionary<string, string>();
#else
    private readonly static IDictionary<string, string> UnixPtyEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "TERM", "xterm-256color" },

        // Make sure we didn't start our server from inside tmux.
        { "TMUX", string.Empty },
        { "TMUX_PANE", string.Empty },

        // Make sure we didn't start our server from inside screen.
        // http://web.mit.edu/gnu/doc/html/screen_20.html
        { "STY", string.Empty },
        { "WINDOW", string.Empty },

        // These variables that might confuse our terminal
        { "WINDOWID", string.Empty },
        { "TERMCAP", string.Empty },
        { "COLUMNS", string.Empty },
        { "LINES", string.Empty },
    };
#endif


#if WINDOWS
    [SupportedOSPlatform("windows10.0.17763")]
#endif
    static PlatformServices()
    {
#if !WINDOWS
        NativeLibrary.SetDllImportResolver(
            typeof(PlatformServices).Assembly,
            (libraryName, _, _) =>
            {
                if (libraryName != "libporta_pty") return 0;

                var fullPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
#if MACOS
                    "osx-" +
#else
                    "linux-" +
#endif

#if ARM64
                    "arm64"
#elif X64
                    "x64"
#else
                    #error Unsupported architecture
#endif
                    ,
                    "native",
#if MACOS
                    "libporta_pty.dylib"
#else
                    "libporta_pty.so"
#endif
                    );
                return File.Exists(fullPath) ? NativeLibrary.Load(fullPath) : IntPtr.Zero;
            });
#endif

#if WINDOWS
        PtyProvider = new Windows.PtyProvider();
        EnvironmentVariableComparer = StringComparer.OrdinalIgnoreCase;
        PtyEnvironment = WindowsPtyEnvironment;
#elif MACOS
        PtyProvider = new Mac.PtyProvider();
        EnvironmentVariableComparer = StringComparer.Ordinal;
        PtyEnvironment = UnixPtyEnvironment;
#elif LINUX
        PtyProvider = new Linux.PtyProvider();
        EnvironmentVariableComparer = StringComparer.Ordinal;
        PtyEnvironment = UnixPtyEnvironment;
#else
        PtyProvider = new Unix.PtyProvider();
        EnvironmentVariableComparer = StringComparer.Ordinal;
        PtyEnvironment = UnixPtyEnvironment;
#endif
    }

    /// <summary>
    ///     Gets the <see cref="IPtyProvider" /> for the current platform.
    /// </summary>
    public static IPtyProvider PtyProvider { get; }

    /// <summary>
    ///     Gets the comparer to determine if two environment variable keys are equivalent on the current platform.
    /// </summary>
    public static StringComparer EnvironmentVariableComparer { get; }

    /// <summary>
    ///     Gets specific environment variables that are needed when spawning the PTY.
    /// </summary>
    public static IDictionary<string, string> PtyEnvironment { get; }
}