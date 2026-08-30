namespace NightGate.Desktop;

public sealed record MonitorPixelBounds(
    int X,
    int Y,
    int Width,
    int Height);

public sealed record MonitorDescriptor(
    string Id,
    MonitorPixelBounds PixelBounds,
    bool IsPrimary,
    MonitorPixelBounds? WorkingAreaPixelBounds = null);

public sealed record OverlayWindowPlacement(
    string MonitorId,
    MonitorPixelBounds PixelBounds,
    bool ShowsExceptionControls);

public interface IMonitorLayoutProvider
{
    IReadOnlyList<MonitorDescriptor> ReadMonitors();
}

public sealed record CommitmentCountdownPlacement(
    string MonitorId,
    MonitorPixelBounds PixelBounds);

public static class CommitmentCountdownLayoutPlanner
{
    public static CommitmentCountdownPlacement Plan(
        IEnumerable<MonitorDescriptor> monitors,
        int windowWidth,
        int windowHeight,
        int margin,
        string? preferredMonitorId = null)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);

        MonitorDescriptor[] snapshot = monitors.ToArray();
        if (snapshot.Length == 0
            || snapshot.Any(monitor => !IsValid(monitor))
            || snapshot.Select(monitor => monitor.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "The countdown topology contains an invalid monitor descriptor.",
                nameof(monitors));
        }

        MonitorDescriptor[] primary = snapshot
            .Where(monitor => monitor.IsPrimary)
            .ToArray();
        if (primary.Length != 1)
        {
            throw new ArgumentException(
                "The countdown topology must contain exactly one primary monitor.",
                nameof(monitors));
        }

        MonitorDescriptor monitor = snapshot.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, preferredMonitorId, StringComparison.OrdinalIgnoreCase))
            ?? primary[0];
        if (!IsUsableWorkArea(
                monitor.PixelBounds,
                monitor.WorkingAreaPixelBounds))
        {
            throw new ArgumentException(
                "The selected monitor has no trustworthy taskbar-excluding work area.",
                nameof(monitors));
        }

        MonitorPixelBounds workArea = monitor.WorkingAreaPixelBounds!;
        int width = Math.Min(windowWidth, workArea.Width);
        int height = Math.Min(windowHeight, workArea.Height);
        int horizontalMargin = Math.Min(
            margin,
            Math.Max(0, (workArea.Width - width) / 2));
        int verticalMargin = Math.Min(
            margin,
            Math.Max(0, (workArea.Height - height) / 2));

        try
        {
            int x = checked(workArea.X + workArea.Width - width - horizontalMargin);
            int y = checked(workArea.Y + verticalMargin);
            return new(
                monitor.Id,
                new MonitorPixelBounds(x, y, width, height));
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "The countdown topology exceeds physical pixel coordinate limits.",
                nameof(monitors),
                exception);
        }
    }

    private static bool IsValid(MonitorDescriptor? monitor) =>
        monitor is not null
        && !string.IsNullOrWhiteSpace(monitor.Id)
        && string.Equals(monitor.Id, monitor.Id.Trim(), StringComparison.Ordinal)
        && monitor.PixelBounds is not null
        && monitor.PixelBounds.Width > 0
        && monitor.PixelBounds.Height > 0;

    private static bool IsUsableWorkArea(
        MonitorPixelBounds monitor,
        MonitorPixelBounds? workArea)
    {
        if (workArea is null || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return false;
        }

        try
        {
            int monitorRight = checked(monitor.X + monitor.Width);
            int monitorBottom = checked(monitor.Y + monitor.Height);
            int workRight = checked(workArea.X + workArea.Width);
            int workBottom = checked(workArea.Y + workArea.Height);
            return workArea.X >= monitor.X
                && workArea.Y >= monitor.Y
                && workRight <= monitorRight
                && workBottom <= monitorBottom;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

/// <summary>
/// Visual-only placement: each visible interval retains its position between
/// twelve-second moves. No wall-clock value influences the movement schedule.
/// </summary>
public sealed class CommitmentCountdownMovementModel
{
    public static readonly TimeSpan MovementInterval = TimeSpan.FromSeconds(12);
    private readonly Func<int, int> _nextIndex;
    private CommitmentCountdownPlacement? _placement;
    private MonitorPixelBounds? _workArea;
    private TimeSpan _highWater;
    private TimeSpan _lastMove;
    private int _margin;

    public CommitmentCountdownMovementModel(Func<int, int>? nextIndex = null)
    {
        _nextIndex = nextIndex ?? Random.Shared.Next;
    }

    public CommitmentCountdownPlacement Update(
        IEnumerable<MonitorDescriptor> monitors,
        int windowWidth,
        int windowHeight,
        int margin,
        TimeSpan monotonicNow,
        string? preferredMonitorId = null)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        MonitorDescriptor[] snapshot = monitors.ToArray();
        _highWater = monotonicNow > _highWater ? monotonicNow : _highWater;
        bool moveDue = _placement is null || _highWater - _lastMove >= MovementInterval;
        string? selectedId = !moveDue && snapshot.Any(monitor =>
            string.Equals(monitor.Id, _placement?.MonitorId, StringComparison.OrdinalIgnoreCase))
            ? _placement!.MonitorId
            : preferredMonitorId;
        CommitmentCountdownPlacement topRight = CommitmentCountdownLayoutPlanner.Plan(
            snapshot, windowWidth, windowHeight, margin, selectedId);
        MonitorPixelBounds workArea = snapshot.Single(monitor =>
            string.Equals(monitor.Id, topRight.MonitorId, StringComparison.OrdinalIgnoreCase))
            .WorkingAreaPixelBounds!;
        bool layoutChanged = _placement is null
            || _placement.MonitorId != topRight.MonitorId
            || _placement.PixelBounds.Width != topRight.PixelBounds.Width
            || _placement.PixelBounds.Height != topRight.PixelBounds.Height
            || _workArea != workArea
            || _margin != margin;
        if (!moveDue && !layoutChanged)
        {
            return _placement!;
        }

        int width = topRight.PixelBounds.Width;
        int height = topRight.PixelBounds.Height;
        int horizontalMargin = Math.Min(margin, (workArea.Width - width) / 2);
        int verticalMargin = Math.Min(margin, (workArea.Height - height) / 2);
        int minX = checked(workArea.X + horizontalMargin);
        int maxX = topRight.PixelBounds.X;
        int minY = topRight.PixelBounds.Y;
        int maxY = checked(workArea.Y + workArea.Height - height - verticalMargin);
        int[] xs = [minX, minX + (maxX - minX) / 2, maxX];
        int[] ys = [minY, minY + (maxY - minY) / 2, maxY];
        MonitorPixelBounds[] candidates = xs.SelectMany(x => ys.Select(y =>
            new MonitorPixelBounds(x, y, width, height))).Distinct().ToArray();

        if (_placement?.MonitorId == topRight.MonitorId && candidates.Length > 1)
        {
            MonitorPixelBounds previous = _placement.PixelBounds;
            long minimumDistance = Math.Min(240, Math.Max(maxX - minX, maxY - minY) / 2);
            MonitorPixelBounds[] distant = candidates.Where(candidate =>
                DistanceSquared(candidate, previous) >= minimumDistance * minimumDistance
                && (candidate.X != previous.X || candidate.Y != previous.Y)).ToArray();
            if (distant.Length > 0)
            {
                candidates = distant;
            }
        }

        int index = _nextIndex(candidates.Length);
        if (index < 0 || index >= candidates.Length)
        {
            throw new InvalidOperationException("The countdown random source returned an invalid index.");
        }

        _placement = new(topRight.MonitorId, candidates[index]);
        _workArea = workArea;
        _margin = margin;
        _lastMove = _highWater;
        return _placement;
    }

    public void Reset()
    {
        _placement = null;
        _workArea = null;
        _highWater = TimeSpan.Zero;
        _lastMove = TimeSpan.Zero;
        _margin = 0;
    }

    private static double DistanceSquared(MonitorPixelBounds left, MonitorPixelBounds right)
    {
        double x = (double)left.X - right.X;
        double y = (double)left.Y - right.Y;
        return x * x + y * y;
    }
}

public static class OverlayLayoutPlanner
{
    public static IReadOnlyList<OverlayWindowPlacement> Plan(
        IEnumerable<MonitorDescriptor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        MonitorDescriptor[] snapshot = monitors.ToArray();
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorDescriptor? monitor in snapshot)
        {
            if (monitor is null
                || string.IsNullOrWhiteSpace(monitor.Id)
                || !string.Equals(monitor.Id, monitor.Id.Trim(), StringComparison.Ordinal)
                || !ids.Add(monitor.Id)
                || monitor.PixelBounds is null
                || monitor.PixelBounds.Width <= 0
                || monitor.PixelBounds.Height <= 0)
            {
                throw new ArgumentException(
                    "The overlay topology contains an invalid monitor descriptor.",
                    nameof(monitors));
            }
        }

        if (snapshot.Count(monitor => monitor.IsPrimary) != 1)
        {
            throw new ArgumentException(
                "The overlay topology must contain exactly one primary monitor.",
                nameof(monitors));
        }

        return snapshot
            .Select(monitor => new OverlayWindowPlacement(
                monitor.Id,
                monitor.PixelBounds,
                monitor.IsPrimary))
            .ToArray();
    }
}
