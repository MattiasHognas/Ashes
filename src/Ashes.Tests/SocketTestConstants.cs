namespace Ashes.Tests;

internal static class SocketTestConstants
{
    internal static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ReadChunkTimeout = TimeSpan.FromSeconds(15);
    internal const int SocketTimeoutMs = 15_000;
}
