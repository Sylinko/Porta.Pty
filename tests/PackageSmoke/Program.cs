using Porta.Pty;

var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Mac" : "Linux";
var assembly = typeof(PtyProvider).Assembly;
if (assembly.GetType($"Porta.Pty.{platform}.PtyProvider") is null)
{
    throw new InvalidOperationException($"NuGet selected the wrong managed implementation for {platform}.");
}

if (OperatingSystem.IsWindows())
{
    foreach (var arch in new[] { "x64", "arm64" })
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, arch, "OpenConsole.exe")))
        {
            throw new FileNotFoundException($"Missing ConPTY {arch} host.");
        }
    }
}

using var connection = await PtyProvider.SpawnAsync(new PtyOptions
{
    App = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : "/bin/sh",
    CommandLine = OperatingSystem.IsWindows() ? ["/d", "/c", "exit 0"] : ["-c", "exit 0"],
    VerbatimCommandLine = true,
    Cwd = AppContext.BaseDirectory,
    Cols = 80,
    Rows = 24,
}, CancellationToken.None);

var hasExited = connection.WaitForExit(10_000);
if (!hasExited || connection.ExitCode != 0)
{
    throw new InvalidOperationException($"The packaged PTY implementation could not run a shell successfully: exited={hasExited}, exit code={connection.ExitCode}.");
}

Console.WriteLine($"Package smoke passed: {platform}, process {connection.Pid}.");
