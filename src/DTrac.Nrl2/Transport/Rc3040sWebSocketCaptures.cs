using System.Diagnostics;

namespace DTrac.Nrl2.Transport;

/// <summary>
/// A burst of read-only WebSocket text frames. No frame boundary is treated as
/// a transaction boundary, and a quiet gap is NOT a firmware-level ACK.
/// </summary>
public sealed record Rc3040sWebSocketBurst(
    IReadOnlyList<string> Frames,
    bool QuietGapObserved,
    bool LimitsReached,
    TimeSpan Duration);

public static class Rc3040sWebSocketCaptures
{
    /// <summary>
    /// Sends exactly one known read-only C2 request and gathers subsequent WS
    /// messages until an idle gap (after first message), overall deadline or
    /// safe cap. It never sends EEPROM/AT writes or motor movement.
    /// </summary>
    public static async Task<Rc3040sWebSocketBurst> CaptureReadOnlyC2Async(
        Rc3040sWebSocketClient client,
        TimeSpan firstMessageTimeout,
        TimeSpan interFrameQuiet,
        TimeSpan overallTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (firstMessageTimeout <= TimeSpan.Zero || firstMessageTimeout > TimeSpan.FromSeconds(10) ||
            interFrameQuiet < TimeSpan.FromMilliseconds(100) ||
            interFrameQuiet > TimeSpan.FromSeconds(5) ||
            overallTimeout < firstMessageTimeout || overallTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(overallTimeout), "Unreasonable bounded capture timeout.");

        const int maxMessages = 32, maxChars = 16384;
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        int chars = 0;
        bool quiet = false, limited = false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(overallTimeout);
        await client.SendTextAsync("C2\r", cancellationToken: deadline.Token).ConfigureAwait(false);

        while (!deadline.IsCancellationRequested)
        {
            if (messages.Count >= maxMessages || chars >= maxChars)
            {
                limited = true;
                break;
            }
            TimeSpan remaining = overallTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            TimeSpan timeout = messages.Count == 0 ? firstMessageTimeout : interFrameQuiet;
            if (timeout > remaining) timeout = remaining;
            if (timeout <= TimeSpan.Zero) break;

            string? frame;
            try
            {
                frame = await client.ReceiveTextAsync(timeout, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (frame is null)
            {
                quiet = messages.Count > 0;
                break;
            }
            chars += frame.Length;
            if (chars > maxChars)
            {
                limited = true;
                break;
            }
            messages.Add(frame);
        }
        stopwatch.Stop();
        return new Rc3040sWebSocketBurst(messages.AsReadOnly(), quiet, limited, stopwatch.Elapsed);
    }
}
