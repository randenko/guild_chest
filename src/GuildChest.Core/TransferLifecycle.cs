using System;

namespace GuildChest.Core;

public enum TransferPhase { AwaitingAcknowledgement, Accepted, Applied, Rejected, Completed }

/// <summary>Separates host acknowledgement, atomic local mutation and completion.</summary>
public sealed class TransferLifecycle
{
    public TransferPhase Phase { get; private set; }
    public bool Acknowledge(bool accepted)
    {
        if (Phase != TransferPhase.AwaitingAcknowledgement) return false;
        Phase = accepted ? TransferPhase.Accepted : TransferPhase.Rejected;
        return true;
    }
    /// <summary>Prepare allocations before mutation; notifications belong after this call.</summary>
    public bool TryApply(Action mutation)
    {
        if (Phase != TransferPhase.Accepted) return false;
        mutation();
        Phase = TransferPhase.Applied;
        return true;
    }
    public bool Finish()
    {
        if (Phase != TransferPhase.Applied && Phase != TransferPhase.Rejected) return false;
        Phase = TransferPhase.Completed;
        return true;
    }
}

/// <summary>A completion handler cannot turn its own failure into a second outcome.</summary>
public sealed class TransferCompletion
{
    private Action<bool, string>? callback;
    private readonly Action<Exception> logError;
    public bool IsCompleted { get; private set; }
    public TransferCompletion(Action<bool, string>? callback, Action<Exception> logError)
    { this.callback = callback; this.logError = logError; }
    public void Complete(bool accepted, string reason)
    {
        if (IsCompleted) return;
        IsCompleted = true;
        var handler = callback; callback = null;
        try { handler?.Invoke(accepted, reason); }
        catch (Exception exception) { logError(exception); }
    }
}
