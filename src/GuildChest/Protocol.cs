using GuildChest.Core;

namespace GuildChest;

// Numeric operations and the header layout are part of the V1 wire protocol.
internal enum Operation { Open = 0, Commit = 1, Heartbeat = 2, Close = 3, Peek = 4, OpenAutomation = 5 }

internal static class Protocol
{
    internal const string RequestRpc = "GuildChestRequestsV1", ReplyRpc = "GuildChestRepliesV1";
    internal const int Width = 8, Height = 4, MaxBytes = 1024 * 1024, HeaderAllowance = 4096;
    internal const float OpenTimeout = (float)SharedStore.LeaseSeconds, HeartbeatInterval = 5f, RetryInterval = 2f;
    internal const float SessionTimeout = OpenTimeout + HeartbeatInterval;
    internal const float PollInterval = 1f, PeekTimeout = 10f, PreviewLifetime = 3f;

    internal static ZPackage Header(Operation operation, ZDOID chest, string token, long sequence)
    {
        var package = new ZPackage(); package.Write((int)operation); package.Write(chest);
        package.Write(token); package.Write(sequence); return package;
    }
}
