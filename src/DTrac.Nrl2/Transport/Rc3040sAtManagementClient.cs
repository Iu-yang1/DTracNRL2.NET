using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Rotator;

namespace DTrac.Nrl2.Transport;

/// <summary>
/// RC-3040S Socket0/WebSocket AT configuration operations. This is separate from
/// GS-232 motor control on TCP/60020. Evidence for speed/RESET command dispatch
/// comes from 251217 firmware disassembly; user hardware reports 260223_C.
/// Actual AT speed writes and command acknowledgement are NOT HIL validated.
/// </summary>
public enum Rc3040sAtOperation
{
    AzimuthSpeed,
    ElevationSpeed,
    Restart,
    DiagnosticEcho
}

/// <summary>
/// A one-use, command-exact permit. Callers must obtain affirmative operator
/// consent for one deliberate request; do not persist permits or recreate them
/// automatically on retry/reconnect. This is a software safety interlock, NOT
/// an authentication mechanism or substitute for physical emergency stop.
/// </summary>
public sealed class Rc3040sAtWritePermit
{
    private int consumed;
    private readonly byte[] command;

    private Rc3040sAtWritePermit(Rc3040sAtOperation operation, byte[] command,
        string expectedFirmware, bool acknowledgeEffects, bool acknowledgeUnverifiedProtocol)
    {
        if (!acknowledgeEffects || !acknowledgeUnverifiedProtocol)
            throw new InvalidOperationException(
                "A human must confirm both possible device effects and unverified firmware behavior.");
        if (string.IsNullOrWhiteSpace(expectedFirmware) ||
            !Regex.IsMatch(expectedFirmware,
                @"^RC_3040S_67S_8H_[0-9]{6}_[A-Za-z0-9]+$",
                RegexOptions.CultureInvariant))
            throw new ArgumentException("Pin the exact firmware banner returned by this controller.",
                nameof(expectedFirmware));
        Operation = operation;
        this.command = (byte[])command.Clone();
        ExpectedFirmware = expectedFirmware;
    }

    public Rc3040sAtOperation Operation { get; }
    public string ExpectedFirmware { get; }
    public ushort? RequestedRawSpeed { get; private init; }

    // Only for the diagnostic AT+TEST operation. It is an exact per-request
    // echoed text marker, not a generic acknowledgement for EEPROM writes.
    internal string? ExpectedEchoPayload { get; private init; }

    /// <summary>
    /// Raw speed is an uncalibrated frequency/firmware value; 1..65535 is only
    /// encoding safety, NOT a proven safe axis speed. May persist to EEPROM.
    /// </summary>
    public static Rc3040sAtWritePermit ForSpeed(
        bool azimuth, ushort rawValue, string expectedFirmware,
        bool confirmPersistentSettingChange, bool confirmUnknownMechanicalEffect)
    {
        byte[] command = azimuth
            ? Rc3040sFirmwareCommands.SetAzimuthSpeed(rawValue)
            : Rc3040sFirmwareCommands.SetElevationSpeed(rawValue);
        return new Rc3040sAtWritePermit(azimuth ? Rc3040sAtOperation.AzimuthSpeed
            : Rc3040sAtOperation.ElevationSpeed, command, expectedFirmware,
            confirmPersistentSettingChange, confirmUnknownMechanicalEffect)
        { RequestedRawSpeed = rawValue };
    }

    /// <summary>Could disconnect control and move hardware into unsafe state; never run automatically.</summary>
    public static Rc3040sAtWritePermit ForRestart(
        string expectedFirmware, bool confirmDisconnectionRisk, bool confirmUnverifiedProtocol) =>
        new(Rc3040sAtOperation.Restart, Rc3040sFirmwareCommands.Restart(),
            expectedFirmware, confirmDisconnectionRisk, confirmUnverifiedProtocol);

    /// <summary>
    /// 251217 disassembly suggests TEST is an echo path; 260223 hardware behavior
    /// is unverified. Even this diagnostic requires explicit operator consent.
    /// </summary>
    public static Rc3040sAtWritePermit ForDiagnosticEcho(
        string payload, string expectedFirmware,
        bool confirmPotentialDeviceEffects, bool confirmUnverifiedProtocol) =>
        new(Rc3040sAtOperation.DiagnosticEcho,
            Rc3040sFirmwareCommands.DiagnosticEcho(payload),
            expectedFirmware, confirmPotentialDeviceEffects, confirmUnverifiedProtocol)
        { ExpectedEchoPayload = payload };

    internal byte[] ConsumeExactlyOnce(string actualFirmware)
    {
        if (!string.Equals(actualFirmware, ExpectedFirmware, StringComparison.Ordinal))
            throw new InvalidOperationException("Connected controller firmware differs from the approved version.");
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0)
            throw new InvalidOperationException("AT command permit already consumed; no automatic retries allowed.");
        return (byte[])command.Clone();
    }
}

/// <summary>
/// A write finishing on the network is NOT a verified firmware ACK, EEPROM
/// commit, safe motor speed, or confirmed reboot. Captured messages are redacted.
/// </summary>
public sealed record Rc3040sAtDispatchObservation(
    Rc3040sAtOperation Operation,
    string FirmwareVersion,
    bool WebSocketSendCompleted,
    IReadOnlyList<string> ObservedMessages,
    bool DeviceAcknowledgementVerified,
    bool PersistentSettingVerified,
    bool ReadbackAvailable,
    bool DiagnosticEchoMatched = false);

/// <summary>
/// Independent AT speed-write verification result. Updated means only that a
/// later configuration C2 reported the requested raw value, not that EEPROM
/// or the physical motor speed was independently validated.
/// </summary>
public enum Rc3040sSpeedReadbackOutcome
{
    Updated,
    Unchanged,
    ReadbackUnavailable,
    UnexpectedValue,
    IdentityMismatch,
    UncertainAfterWrite
}

public sealed record Rc3040sSpeedWriteReadback(
    Rc3040sConfigurationSnapshot Before,
    Rc3040sConfigurationSnapshot? After,
    Rc3040sSpeedReadbackOutcome Outcome,
    ushort RequestedRawSpeed,
    bool CommandAttempted,
    bool NetworkSendCompleted,
    bool PersistentSettingVerified,
    string Detail);

/// <summary>
/// Two-phase management transport: C2 configuration identification first,
/// then one explicitly approved AT command on the same connection.
/// Does not perform automatic retries or reconnects; it never calls TCP/60020.
/// </summary>
public sealed class Rc3040sAtManagementClient : IAsyncDisposable
{
    private readonly Rc3040sWebSocketClient ws;
    private bool disposed;
    private int commandAttempted;

    private Rc3040sAtManagementClient(Rc3040sWebSocketClient ws) => this.ws = ws;

    public static async Task<Rc3040sAtManagementClient> ConnectAsync(
        string host, int port = Rc3040sNetworkPorts.ManagementTcpPort,
        CancellationToken cancellationToken = default) =>
        new(await Rc3040sWebSocketClient.ConnectAsync(host, port, cancellationToken)
            .ConfigureAwait(false));

    /// <summary>Read-only preflight. Sends exactly one C2, never writes AT settings.</summary>
    public async Task<Rc3040sConfigurationSnapshot> ReadConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Volatile.Read(ref commandAttempted) != 0)
            throw new InvalidOperationException("Configuration should be read before any AT write.");
        var burst = await Rc3040sWebSocketCaptures.CaptureReadOnlyC2Async(ws,
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(700),
            TimeSpan.FromSeconds(9), cancellationToken).ConfigureAwait(false);
        if (burst.LimitsReached || !burst.QuietGapObserved)
            throw new InvalidDataException("Configuration dump incomplete or exceeded safety bounds.");
        return Rc3040sConfigurationDump.Parse(burst.Frames);
    }

    /// <summary>
    /// One operation only. Before writing, the method fetches a fresh complete
    /// configuration and insists that firmware is EXACTLY the consented version.
    /// Never assumes "OK" or a socket-send completion proves EEPROM/motor effects.
    /// Caller must not retry after an ambiguous failure.
    /// </summary>
    public async Task<Rc3040sAtDispatchObservation> SendApprovedAsync(
        Rc3040sAtWritePermit permit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permit);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Exchange(ref commandAttempted, 1) != 0)
            throw new InvalidOperationException("At most one AT configuration operation per connection.");

        // Mark before any network I/O to prevent reentrancy or ambiguous retry.
        var config = await ReadConfigurationForWriteAsync(cancellationToken).ConfigureAwait(false);
        if (config.FirmwareVersion is null || !config.SawNetworkSection ||
            config.RecognizedFieldCount < 3)
            throw new InvalidDataException("Cannot identify target firmware from a complete read-only dump.");
        byte[] command = permit.ConsumeExactlyOnce(config.FirmwareVersion);

        // Exact one-time operator-approved AT text command. Firmware receipt and
        // eventual EEPROM commit remain unknown in the absence of verified ACK.
        await ws.SendFirmwareCommandAsync(command, true, cancellationToken).ConfigureAwait(false);

        var observed = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        while (observed.Count < 8 && !deadline.IsCancellationRequested)
        {
            string? frame;
            try
            {
                frame = await ws.ReceiveTextAsync(
                    observed.Count == 0 ? TimeSpan.FromSeconds(2) :
                    TimeSpan.FromMilliseconds(450), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            catch (WebSocketException) { break; } // RESET may sever connection immediately.
            if (frame is null) break;
            observed.Add(RedactAtResponse(frame));
        }
        bool exactEcho = permit.Operation == Rc3040sAtOperation.DiagnosticEcho &&
            permit.ExpectedEchoPayload is not null &&
            observed.Any(frame => frame.TrimEnd('\r', '\n')
                .Equals(permit.ExpectedEchoPayload, StringComparison.Ordinal));
        return new Rc3040sAtDispatchObservation(
            permit.Operation, config.FirmwareVersion, true, observed.AsReadOnly(),
            DeviceAcknowledgementVerified: false,
            PersistentSettingVerified: false,
            ReadbackAvailable: false,
            DiagnosticEchoMatched: exactEcho);
    }

    /// <summary>
    /// One connection for: C2 preflight, one explicitly permitted speed write,
    /// and fresh C2 readback. Never reconnects or retries an AT command.
    /// The onWriteAttempt callback is invoked BEFORE network transmission so
    /// callers may fail closed if sending or readback then throws.
    /// </summary>
    public async Task<Rc3040sSpeedWriteReadback> SendSpeedAndReadbackAsync(
        Rc3040sAtWritePermit permit,
        string? expectedMaskedDeviceId = null,
        Action? onWriteAttempt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permit);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (permit.Operation is not (Rc3040sAtOperation.AzimuthSpeed or
            Rc3040sAtOperation.ElevationSpeed) || permit.RequestedRawSpeed is null)
            throw new ArgumentException("A one-shot AZ/EL speed permit is required.", nameof(permit));
        if (Interlocked.Exchange(ref commandAttempted, 1) != 0)
            throw new InvalidOperationException("One AT configuration operation per session.");

        var before = await ReadConfigurationForWriteAsync(cancellationToken).ConfigureAwait(false);
        if (before.FirmwareVersion is null || !before.SawNetworkSection ||
            before.RecognizedFieldCount < 3 || before.MaskedDeviceId is null)
            throw new InvalidDataException("Incomplete firmware/device preflight; no AT was transmitted.");

        // Reject a stale UI selection before consuming the permit or sending.
        if (expectedMaskedDeviceId is not null &&
            !string.Equals(expectedMaskedDeviceId, before.MaskedDeviceId,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Connected device differs from the selected device.");
        if (before.FirmwareVersion != permit.ExpectedFirmware)
            throw new InvalidOperationException("Firmware mismatch; no AT was transmitted.");

        bool azimuth = permit.Operation == Rc3040sAtOperation.AzimuthSpeed;
        ushort? existing = azimuth ? before.AzimuthSpeedRaw : before.ElevationSpeedRaw;
        if (existing is null)
            throw new InvalidDataException("Preflight speed unavailable; no AT was transmitted.");
        ushort requested = permit.RequestedRawSpeed.Value;
        if (existing == requested)
            return new Rc3040sSpeedWriteReadback(before, before,
                Rc3040sSpeedReadbackOutcome.Unchanged, requested,
                false, false, false, "Already at requested raw setting; no AT transmitted.");

        // The permit is consumed once, and may never be retried implicitly.
        byte[] command = permit.ConsumeExactlyOnce(before.FirmwareVersion);
        onWriteAttempt?.Invoke(); // The caller must mark potential transmission here.
        bool sendCompleted = false;
        try
        {
            await ws.SendFirmwareCommandAsync(command, true, cancellationToken).ConfigureAwait(false);
            sendCompleted = true;
            // Do NOT reconnect: this C2 is a new read within the existing WS.
            var after = await ReadConfigurationForWriteAsync(cancellationToken).ConfigureAwait(false);
            if (after.FirmwareVersion != before.FirmwareVersion ||
                after.MaskedDeviceId != before.MaskedDeviceId)
                return new Rc3040sSpeedWriteReadback(before, after,
                    Rc3040sSpeedReadbackOutcome.IdentityMismatch, requested,
                    true, true, false, "Firmware/device changed during readback.");
            ushort? value = azimuth ? after.AzimuthSpeedRaw : after.ElevationSpeedRaw;
            var outcome = value is null
                ? Rc3040sSpeedReadbackOutcome.ReadbackUnavailable
                : value == requested
                    ? Rc3040sSpeedReadbackOutcome.Updated
                    : value == existing
                        ? Rc3040sSpeedReadbackOutcome.Unchanged
                        : Rc3040sSpeedReadbackOutcome.UnexpectedValue;
            string explanation = outcome switch
            {
                Rc3040sSpeedReadbackOutcome.Updated =>
                    "New C2 reports the requested setting; nonvolatile persistence unverified.",
                Rc3040sSpeedReadbackOutcome.Unchanged => "New C2 reports the previous speed.",
                Rc3040sSpeedReadbackOutcome.ReadbackUnavailable =>
                    "New C2 did not include a speed field.",
                _ => "New C2 reports an unexpected value."
            };
            return new Rc3040sSpeedWriteReadback(before, after, outcome,
                requested, true, true, false, explanation);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or
            TimeoutException or OperationCanceledException or InvalidDataException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return new Rc3040sSpeedWriteReadback(before, null,
                Rc3040sSpeedReadbackOutcome.UncertainAfterWrite, requested,
                true, sendCompleted, false,
                $"AT may have been transmitted; readback uncertain ({ex.GetType().Name}). Do not retry.");
        }
    }

    private static string RedactAtResponse(string value)
    {
        string masked = Rc3040sFirmwareBanner.RedactDeviceIds(value);
        masked = Regex.Replace(masked, @"(?i)\b(?:[0-9a-f]{2}:){5}[0-9a-f]{2}\b",
            match => "**:**:**:**:" + match.Value[^5..]);
        return Regex.Replace(masked,
            @"(?im)(AT\+(?:CALL|SSID|PASS|PASSWORD|KEY)\s*=)[^\r\n]*",
            "$1[REDACTED]");
    }

    private async Task<Rc3040sConfigurationSnapshot> ReadConfigurationForWriteAsync(
        CancellationToken cancellationToken)
    {
        var burst = await Rc3040sWebSocketCaptures.CaptureReadOnlyC2Async(ws,
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(700),
            TimeSpan.FromSeconds(9), cancellationToken).ConfigureAwait(false);
        if (!burst.QuietGapObserved || burst.LimitsReached)
            throw new InvalidDataException("Cannot establish complete management preflight.");
        return Rc3040sConfigurationDump.Parse(burst.Frames);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await ws.DisposeAsync().ConfigureAwait(false);
    }
}
