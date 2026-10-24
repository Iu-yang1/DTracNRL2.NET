using System.Net.WebSockets;
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
            confirmPersistentSettingChange, confirmUnknownMechanicalEffect);
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
            expectedFirmware, confirmPotentialDeviceEffects, confirmUnverifiedProtocol);

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
    bool ReadbackAvailable);

/// <summary>
/// Two-phase management transport: C2 configuration identification first,
/// then one explicitly approved AT command on the same connection.
/// Does not perform automatic retries or reconnects; it never calls TCP/60020.
/// </summary>
public sealed class Rc3040sAtManagementClient : IAsyncDisposable
{
    private readonly Rc3040sWebSocketClient ws;
    private bool disposed;
    private bool commandAttempted;

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
        if (commandAttempted)
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
        if (commandAttempted)
            throw new InvalidOperationException("At most one AT configuration operation per connection.");

        // Mark before any network I/O to prevent reentrancy or ambiguous retry.
        commandAttempted = true;
        var config = await ReadConfigurationForWriteAsync(cancellationToken).ConfigureAwait(false);
        if (config.FirmwareVersion is null || !config.SawNetworkSection ||
            config.RecognizedFieldCount < 3)
            throw new InvalidDataException("Cannot authenticate target firmware from a complete read-only dump.");
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
            observed.Add(Rc3040sFirmwareBanner.RedactDeviceIds(frame));
        }
        return new Rc3040sAtDispatchObservation(
            permit.Operation, config.FirmwareVersion, true, observed.AsReadOnly(),
            DeviceAcknowledgementVerified: false,
            PersistentSettingVerified: false,
            ReadbackAvailable: false);
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
