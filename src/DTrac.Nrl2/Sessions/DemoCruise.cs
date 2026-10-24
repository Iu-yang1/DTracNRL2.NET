using DTrac.Nrl2.Rotator;

namespace DTrac.Nrl2.Sessions;

public enum CruiseState { Waiting, NextWaypoint, Completed, Aborted }

/// <summary>
/// Motion planning only: caller must enforce limits, live tracking lockout and hardware STOP.
/// This is NOT a firmware-native AT+TEST cruise or a motor acceleration controller.
/// </summary>
public sealed class DemoCruise
{
    private readonly IReadOnlyList<RotatorPosition> points;
    private readonly TimeSpan dwell;
    private DateTimeOffset segmentStart;
    private DateTimeOffset lastFeedback;
    private DateTimeOffset? firstArrival;
    private int matchingReadings;
    public int Index { get; private set; }
    public int Count => points.Count;
    public RotatorPosition Target => points[Index];

    private DemoCruise(IReadOnlyList<RotatorPosition> points, TimeSpan dwell, DateTimeOffset now)
    {
        this.points = points;
        this.dwell = dwell;
        segmentStart = now;
    }

    public static DemoCruise Create(double startAz, double endAz, double elevation,
        double step, int cycles, TimeSpan dwell, RotatorPosition max, DateTimeOffset now)
    {
        if (!double.IsFinite(startAz) || !double.IsFinite(endAz) ||
            !double.IsFinite(elevation) || !double.IsFinite(step) ||
            startAz < 0 || startAz >= endAz || endAz > max.Azimuth ||
            elevation < 0 || elevation > max.Elevation ||
            step is < 1 or > 90 || cycles is < 1 or > 5 ||
            dwell < TimeSpan.FromSeconds(1) || dwell > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(startAz), "Unsafe demo sweep.");
        var angles = new List<double>();
        for (double az = startAz; az < endAz; az += step)
        {
            if (angles.Count > 450) throw new ArgumentOutOfRangeException(nameof(step));
            angles.Add(az);
        }
        angles.Add(endAz);
        var waypoints = new List<RotatorPosition>();
        for (int c = 0; c < cycles; c++)
        {
            foreach (var az in angles) waypoints.Add(new(az, elevation));
            for (int j = angles.Count - 2; j >= 0; --j) waypoints.Add(new(angles[j], elevation));
        }
        return new DemoCruise(waypoints, dwell, now);
    }

    public CruiseState Observe(DateTimeOffset now, DateTimeOffset feedbackAt,
        RotatorPosition actual, bool feedbackHealthy)
    {
        if (!feedbackHealthy || now - segmentStart > TimeSpan.FromMinutes(2))
            return CruiseState.Aborted;
        if (feedbackAt <= lastFeedback || feedbackAt > now ||
            now - feedbackAt > TimeSpan.FromSeconds(3))
            return CruiseState.Waiting;
        lastFeedback = feedbackAt;
        if (!double.IsFinite(actual.Azimuth) || !double.IsFinite(actual.Elevation) ||
            Math.Abs(actual.Azimuth - Target.Azimuth) > 1.5 ||
            Math.Abs(actual.Elevation - Target.Elevation) > 1.5)
        {
            matchingReadings = 0;
            firstArrival = null;
            return CruiseState.Waiting;
        }
        if (++matchingReadings == 1) firstArrival = now;
        if (matchingReadings < 2 || now - firstArrival!.Value < dwell)
            return CruiseState.Waiting;
        if (++Index == points.Count) return CruiseState.Completed;
        matchingReadings = 0;
        firstArrival = null;
        segmentStart = now;
        return CruiseState.NextWaypoint;
    }
}
