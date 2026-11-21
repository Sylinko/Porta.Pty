namespace Porta.Pty;

/// <summary>How observation of the top-level process ended. A signal is not a normal exit code.</summary>
public enum PtyTerminationKind
{
    Exited,
    Signaled,
    ObservationFailed,
}

/// <summary>
/// The native exit code, terminating Unix signal, or operating-system error from observing the process.
/// </summary>
public readonly record struct PtyTermination(PtyTerminationKind Kind, int Value);
