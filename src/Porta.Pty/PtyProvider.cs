// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Diagnostics;

namespace Porta.Pty;

/// <summary>
///     Provides the ability to spawn new processes under a pseudoterminal.
/// </summary>
public static class PtyProvider
{
    private readonly static TraceSource Trace = new(nameof(PtyProvider));

    /// <summary>
    ///     Spawn a new process connected to a pseudoterminal.
    /// </summary>
    /// <param name="options">The set of options for creating the pseudoterminal.</param>
    /// <param name="cancellationToken">The token to cancel process creation early.</param>
    /// <returns>A <see cref="Task{IPtyConnection}" /> that completes once the process has spawned.</returns>
    public static Task<IPtyConnection> SpawnAsync(
        PtyOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.App, nameof(options.App));
        ArgumentException.ThrowIfNullOrEmpty(options.Cwd, nameof(options.Cwd));
        ArgumentNullException.ThrowIfNull(options.CommandLine, nameof(options.CommandLine));
        ArgumentNullException.ThrowIfNull(options.Environment, nameof(options.Environment));

        var environment = MergeEnvironment(PlatformServices.PtyEnvironment, null);
        environment = MergeEnvironment(options.Environment, environment);

        options.Environment = environment;

        return PlatformServices.PtyProvider.StartTerminalAsync(options, Trace, cancellationToken);
    }

    private static IDictionary<string, string> MergeEnvironment(
        IDictionary<string, string> environmentToMerge,
        IDictionary<string, string>? environment)
    {
        if (environment == null)
        {
            environment = new Dictionary<string, string>(PlatformServices.EnvironmentVariableComparer);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var (key, value) = (entry.Key.ToString(), entry.Value?.ToString());
                if (string.IsNullOrEmpty(key) || value is null) continue;

                environment[key] = value;
            }
        }

        foreach (var kvp in environmentToMerge)
        {
            if (string.IsNullOrEmpty(kvp.Value))
            {
                environment.Remove(kvp.Key);
            }
            else
            {
                environment[kvp.Key] = kvp.Value;
            }
        }

        return environment;
    }
}