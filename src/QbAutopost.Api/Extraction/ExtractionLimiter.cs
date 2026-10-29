namespace QbAutopost.Api.Extraction;

/// <summary>
/// Dashboard-api-m12 6.1: per user, one extraction in flight and at most <c>maxPerHour</c> started in any rolling hour.
/// In memory; a restart resets it (accepted by the spec). It sits beside <c>RateLimitPolicy</c>, which still applies.
/// </summary>
public sealed class ExtractionLimiter(Func<int> maxPerHour)
{
    /// <summary>The <c>Retry-After</c> of a refusal because one extraction is already running (6.1).</summary>
    public static readonly TimeSpan InFlightRetryAfter = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, UserState> _users = new(StringComparer.Ordinal);

    public ExtractionLimiter(int maxPerHour)
        : this(() => maxPerHour)
    {
    }

    /// <summary>
    /// Starts an extraction for <paramref name="userId"/> at <paramref name="nowUtc"/>: a handle to dispose when it
    /// ends, or null when refused, with <paramref name="retryAfter"/> saying for how long.
    /// </summary>
    public IDisposable? TryStart(string userId, DateTime nowUtc, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(userId, out var state))
            {
                state = new UserState();
                _users[userId] = state;
            }

            state.Starts.RemoveAll(t => t <= nowUtc - Window);
            if (state.InFlight)
            {
                retryAfter = InFlightRetryAfter;
                return null;
            }

            var max = Math.Max(0, maxPerHour());
            if (state.Starts.Count >= max)
            {
                // Seconds until the oldest start in the window leaves it; never less than one.
                var oldest = state.Starts.Count > 0 ? state.Starts.Min() : nowUtc;
                var wait = oldest + Window - nowUtc;
                retryAfter = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
                return null;
            }

            state.InFlight = true;
            state.Starts.Add(nowUtc);
            retryAfter = TimeSpan.Zero;
            return new Handle(this, userId);
        }
    }

    private void End(string userId)
    {
        lock (_gate)
        {
            if (_users.TryGetValue(userId, out var state))
            {
                state.InFlight = false;
            }
        }
    }

    private sealed class UserState
    {
        public bool InFlight { get; set; }

        public List<DateTime> Starts { get; } = [];
    }

    private sealed class Handle(ExtractionLimiter owner, string userId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End(userId);
            }
        }
    }
}
