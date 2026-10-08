using System;
using GuildChest.Core;
using Xunit;

namespace GuildChest.Tests;

public class TransferLifecycleTests
{
    [Fact]
    public void NotificationFailureAndDuplicateAcknowledgementCannotApplyItemsAgain()
    {
        var lifecycle = new TransferLifecycle();
        int items = 0;
        int errors = 0;
        var completion = new TransferCompletion((_, _) => throw new InvalidOperationException("Consumer notification failed"), _ => errors++);
        Assert.True(lifecycle.Acknowledge(true));
        Assert.True(lifecycle.TryApply(() => items += 7));
        completion.Complete(true, "Accepted");
        Assert.Equal(1, errors);
        Assert.False(lifecycle.Acknowledge(true));
        Assert.False(lifecycle.TryApply(() => items += 7));
        Assert.True(lifecycle.Finish());
        Assert.Equal(7, items);
        Assert.False(lifecycle.TryApply(() => items += 7));
    }

    [Fact]
    public void PreparationFailureCanRetryLocallyWithoutAnotherHostAcknowledgement()
    {
        var lifecycle = new TransferLifecycle();
        lifecycle.Acknowledge(true);
        Assert.Throws<InvalidOperationException>(() => lifecycle.TryApply(() => throw new InvalidOperationException("Prepare failed before mutation")));
        Assert.Equal(TransferPhase.Accepted, lifecycle.Phase);
        int items = 0;
        Assert.True(lifecycle.TryApply(() => items = 7));
        Assert.False(lifecycle.TryApply(() => items = 14));
        Assert.Equal(7, items);
    }

    [Fact]
    public void RejectedOrUnacknowledgedTransferCannotMutateItems()
    {
        var lifecycle = new TransferLifecycle();
        int items = 0;
        Assert.False(lifecycle.TryApply(() => items++));
        Assert.True(lifecycle.Acknowledge(false));
        Assert.False(lifecycle.Acknowledge(true));
        Assert.False(lifecycle.TryApply(() => items++));
        Assert.True(lifecycle.Finish());
        Assert.Equal(0, items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThrowingOrReentrantCallbackReceivesOneOutcome(bool accepted)
    {
        int calls = 0, errors = 0;
        TransferCompletion? completion = null;
        completion = new TransferCompletion((result, reason) =>
        {
            calls++;
            Assert.Equal(accepted, result);
            completion!.Complete(!result, "Reentrant completion");
            throw new InvalidOperationException("Consumer callback failed");
        }, _ => errors++);
        completion.Complete(accepted, "Original outcome");
        completion.Complete(!accepted, "Second outcome");
        Assert.Equal(1, calls);
        Assert.Equal(1, errors);
        Assert.True(completion.IsCompleted);
    }
}
