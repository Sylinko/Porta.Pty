// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Porta.Pty.Tests;

public class PtyTests
{
    private readonly static int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string ShellApp => IsWindows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";

    /// <summary>
    ///     Creates PtyOptions for running a shell command.
    ///     On Windows: cmd.exe /c command
    ///     On Unix: /bin/sh -c command
    /// </summary>
    private static PtyOptions CreateShellCommandOptions(string name, string command)
    {
        return new PtyOptions
        {
            Name = name,
            Cols = 120,
            Rows = 25,
            Cwd = Environment.CurrentDirectory,
            App = ShellApp,
            CommandLine = IsWindows ? ["/c", command] : ["-c", command],
            VerbatimCommandLine = true,
            Environment = new Dictionary<string, string>(),
        };
    }

    /// <summary>
    ///     Creates PtyOptions for an interactive shell session.
    /// </summary>
    private static PtyOptions CreateInteractiveShellOptions(string name)
    {
        return new PtyOptions
        {
            Name = name,
            Cols = 80,
            Rows = 25,
            Cwd = Environment.CurrentDirectory,
            App = ShellApp,
            CommandLine = [],
            Environment = new Dictionary<string, string>(),
        };
    }

    [Test]
    public async Task EchoTest_ReturnsExpectedOutput()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var options = CreateShellCommandOptions("EchoTest", "echo test");

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);

        // Read output until we find expected text or timeout
        var output = await ReadOutputAsync(terminal, "test", TimeSpan.FromSeconds(5));

        Assert.That(output, Does.Contain("test"));

        // Command completes naturally, just wait for exit - no Kill() needed
        terminal.WaitForExit(1000);
    }

    [Test]
    public async Task SpawnAsync_ReturnsPidGreaterThanZero()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var options = CreateShellCommandOptions("PidTest", "echo hello");

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);

        Assert.That(terminal.Pid, Is.GreaterThan(0), "Process ID should be greater than zero");

        // Command completes naturally
        terminal.WaitForExit(1000);
    }

    [Test]
    public async Task ProcessExited_EventIsFired()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);
        var exitedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = CreateShellCommandOptions("ExitEventTest", "echo done");

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);
        terminal.ProcessExited += (_, e) => exitedTcs.TrySetResult(e.ExitCode);

        // Read to ensure command runs
        await ReadOutputAsync(terminal, "done", TimeSpan.FromSeconds(5));

        // Wait for the exit event or timeout
        await using (cts.Token.Register(() => exitedTcs.TrySetCanceled()))
        {
            try
            {
                var exitCode = await exitedTcs.Task;
                Assert.That(exitCode, Is.GreaterThanOrEqualTo(0), $"Exit code should be non-negative, was {exitCode}");
            }
            catch (TaskCanceledException)
            {
                Assert.That(terminal.WaitForExit(1000), Is.True, "Process should have exited");
            }
        }
    }

    [Test]
    public async Task Resize_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var options = CreateShellCommandOptions("ResizeTest", "echo resize");

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);

        Assert.DoesNotThrow(() => terminal.Resize(120, 40));
        Assert.DoesNotThrow(() => terminal.Resize(40, 10));

        terminal.WaitForExit(1000);
    }

    [Test] [Ignore("Not reliable on CI server")]
    public async Task Kill_TerminatesProcess()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var options = CreateInteractiveShellOptions("KillTest");

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);

        await Task.Delay(500, cts.Token);

        Assert.That(terminal.WaitForExit(100), Is.False, "Process should still be running");

        terminal.Kill();

        var exited = terminal.WaitForExit(5000);
        Assert.That(exited, Is.True, "Process should exit after being killed");
    }

    [Test]
    public async Task WaitForExit_ReturnsFalseWhileProcessIsRunning()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var options = CreateInteractiveShellOptions("WaitForExitTest");

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);

        await Task.Delay(500, cts.Token);

        Assert.That(terminal.WaitForExit(100), Is.False, "WaitForExit should return false while process is running");

        // Interactive shell - Kill is appropriate here since it won't exit on its own
        terminal.Kill();
        terminal.WaitForExit(1000);
    }

    [Test]
    public async Task EnvironmentVariables_ArePassedToProcess()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var command = IsWindows ? "echo %MY_TEST_VAR%" : "echo $MY_TEST_VAR";

        var options = new PtyOptions
        {
            Name = "EnvVarTest",
            Cols = 120,
            Rows = 25,
            Cwd = Environment.CurrentDirectory,
            App = ShellApp,
            CommandLine = IsWindows ? ["/c", command] : ["-c", command],
            VerbatimCommandLine = true,
            Environment = new Dictionary<string, string>
            {
                { "MY_TEST_VAR", "custom_value_12345" },
            },
        };

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);

        var output = await ReadOutputAsync(terminal, "custom_value_12345", TimeSpan.FromSeconds(5));

        Assert.That(output, Does.Contain("custom_value_12345"));

        terminal.WaitForExit(1000);
    }

    [Test] [Ignore("Not reliable on CI server")]
    public async Task WorkingDirectory_IsRespected()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var command = IsWindows ? "cd" : "pwd";

        var options = CreateShellCommandOptions("CwdTest", command);

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);

        var output = await ReadOutputAsync(terminal, IsWindows ? "\\" : "/", TimeSpan.FromSeconds(5));

        Assert.That(
            output,
            Does.Contain(Path.DirectorySeparatorChar),
            $"Output should contain path separator. Actual output: '{output}'");

        terminal.WaitForExit(1000);
    }

    [Test]
    public void SpawnAsync_ThrowsOnEmptyApp()
    {
        var options = new PtyOptions
        {
            App = string.Empty,
            Cwd = Environment.CurrentDirectory,
            CommandLine = [],
            Environment = new Dictionary<string, string>(),
        };

        Assert.ThrowsAsync<ArgumentNullException>(() => PtyProvider.SpawnAsync(options, CancellationToken.None));
    }

    [Test]
    public void SpawnAsync_ThrowsOnEmptyCwd()
    {
        var options = new PtyOptions
        {
            App = ShellApp,
            Cwd = string.Empty,
            CommandLine = [],
            Environment = new Dictionary<string, string>(),
        };

        Assert.ThrowsAsync<ArgumentNullException>(() => PtyProvider.SpawnAsync(options, CancellationToken.None));
    }

    [Test]
    public void SpawnAsync_ThrowsOnNullCommandLine()
    {
        var options = new PtyOptions
        {
            App = ShellApp,
            Cwd = Environment.CurrentDirectory,
            CommandLine = null!,
            Environment = new Dictionary<string, string>(),
        };

        Assert.ThrowsAsync<ArgumentNullException>(() => PtyProvider.SpawnAsync(options, CancellationToken.None));
    }

    [Test]
    public void SpawnAsync_ThrowsOnNullEnvironment()
    {
        var options = new PtyOptions
        {
            App = ShellApp,
            Cwd = Environment.CurrentDirectory,
            CommandLine = [],
            Environment = null!,
        };

        Assert.ThrowsAsync<ArgumentNullException>(() => PtyProvider.SpawnAsync(options, CancellationToken.None));
    }

    [Test] [Ignore("Not reliable on CI server")]
    public async Task ExitCode_IsAvailableAfterProcessExits()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var options = CreateShellCommandOptions("ExitCodeTest", "echo success && exit");

        using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);

        await ReadOutputAsync(terminal, "success", TimeSpan.FromSeconds(10));

        Assert.That(terminal.WaitForExit(5000), Is.True, "Process should exit");

        var exitCode = terminal.ExitCode;
        Assert.That(exitCode, Is.GreaterThanOrEqualTo(0), $"Exit code should be non-negative, was {exitCode}");
    }

    /// <summary>
    ///     Reads output from the terminal until the search text is found or timeout.
    /// </summary>
    private async static Task<string> ReadOutputAsync(IPtyConnection terminal, string searchText, TimeSpan timeout)
    {
        var buffer = new byte[4096];
        var output = new StringBuilder();
        var encoding = new UTF8Encoding(false);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var readTask = Task.Run(() => terminal.ReaderStream.Read(buffer, 0, buffer.Length));
                if (await Task.WhenAny(readTask, Task.Delay(500)) != readTask) continue;

                var bytesRead = readTask.Result;
                if (bytesRead <= 0) continue;

                output.Append(encoding.GetString(buffer, 0, bytesRead));
                if (output.ToString().Contains(searchText))
                    break;
            }
            catch
            {
                break;
            }
        }

        return output.ToString();
    }
}