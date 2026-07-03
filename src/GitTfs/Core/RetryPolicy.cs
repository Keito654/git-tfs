using System.Net;
using System.Net.Sockets;

namespace GitTfs.Core
{
    /// <summary>
    /// TFS-independent classification of exceptions into transient (worth retrying)
    /// faults versus permanent ones. Lives in GitTfs core so the classification is
    /// unit-testable without any TFS SDK dependency; the shared <c>Retry</c> utility in
    /// VsCommon composes this with the TFS-specific exception types it can see directly.
    /// </summary>
    public static class RetryPolicy
    {
        /// <summary>
        /// Returns true when <paramref name="exception"/> — or any exception nested in its
        /// <see cref="Exception.InnerException"/> chain — represents a transient network fault
        /// worth retrying (a momentary drop, VPN reconnect, proxy hiccup, or slow response).
        /// The inner-exception chain is walked so wrapped transient faults are still caught.
        /// </summary>
        public static bool IsTransient(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is WebException ||
                    current is SocketException ||
                    current is IOException ||
                    current is TimeoutException)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Computes the sequence of wait intervals for an exponential backoff retry: the n-th
        /// interval is <c>min(initialInterval * 2^n, maxInterval)</c>. The cap keeps a permanent
        /// fault from hanging for minutes — once the doubled interval reaches
        /// <paramref name="maxInterval"/> every subsequent interval plateaus at the cap, so the
        /// total wait is bounded. Returns exactly <paramref name="count"/> intervals.
        /// </summary>
        public static IReadOnlyList<TimeSpan> BackoffIntervals(TimeSpan initialInterval, int count, TimeSpan maxInterval)
        {
            var intervals = new List<TimeSpan>(Math.Max(0, count));
            var current = initialInterval;

            for (var i = 0; i < count; i++)
            {
                var capped = current < maxInterval ? current : maxInterval;
                intervals.Add(capped);

                // Double for the next step, but clamp at the cap so we never overflow past it.
                var doubled = current + current;
                current = (doubled < current /* TimeSpan overflow */ || doubled > maxInterval)
                    ? maxInterval
                    : doubled;
            }

            return intervals;
        }
    }
}
