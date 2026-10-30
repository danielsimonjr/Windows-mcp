namespace WindowsMcp.Tests.Fixtures;

internal static class Mcp20Protocol
{
    public const string Version = "2026-07-28";

    /// <summary>
    /// The newest revision that the server accepts through the <c>initialize</c> handshake. The
    /// source is the server itself: it refuses <see cref="Version"/> through <c>initialize</c>
    /// (error -32022) and lists 2024-11-05, 2025-03-26, 2025-06-18 and 2025-11-25 as supported.
    /// The SDK client reaches <see cref="Version"/> without <c>initialize</c>; a raw
    /// <c>initialize</c>, or a client that uses it, needs this revision.
    /// </summary>
    public const string InitializeHandshakeVersion = "2025-11-25";
}
