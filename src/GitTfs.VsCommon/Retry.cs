using GitTfs.Core;

namespace GitTfs.VsCommon
{
    public static class Retry
    {
        public static void Do(Action action) => Do(action, TimeSpan.FromSeconds(1));

        public static void Do(Action action, TimeSpan retryInterval, int retryCount = 10) => Do<object>(() =>
                                                                                                      {
                                                                                                          action();
                                                                                                          return null;
                                                                                                      }, retryInterval, retryCount);

        public static T Do<T>(Func<T> action) => Do(action, TimeSpan.FromSeconds(1));

        public static T Do<T>(Func<T> action, TimeSpan retryInterval, int retryCount = 10)
            => DoWithIntervals(action, _ => retryInterval, retryCount);

        public static void DoWithBackoff(Action action, TimeSpan initialInterval, int retryCount, TimeSpan maxInterval) => DoWithBackoff<object>(() =>
                                                                                                                                                  {
                                                                                                                                                      action();
                                                                                                                                                      return null;
                                                                                                                                                  }, initialInterval, retryCount, maxInterval);

        /// <summary>
        /// Like <see cref="Do{T}(Func{T}, TimeSpan, int)"/> but waits an exponentially growing
        /// interval between attempts, capped at <paramref name="maxInterval"/> so a permanent
        /// fault fails in bounded time instead of hanging. Used by network-heavy paths (file
        /// content download) where a fixed short interval is not enough to ride out a VPN
        /// reconnect or proxy hiccup that lasts tens of seconds.
        /// </summary>
        public static T DoWithBackoff<T>(Func<T> action, TimeSpan initialInterval, int retryCount, TimeSpan maxInterval)
        {
            var intervals = RetryPolicy.BackoffIntervals(initialInterval, retryCount, maxInterval);
            return DoWithIntervals(action, retry => intervals[retry], retryCount);
        }

        /// <summary>
        /// Shared retry loop: attempts <paramref name="action"/> up to <paramref name="retryCount"/>
        /// times, retrying only faults <see cref="IsRetryable"/> accepts and sleeping
        /// <paramref name="intervalForRetry"/> between attempts (never after the final one). Both the
        /// fixed-interval and exponential-backoff overloads route through here so their retry
        /// classification and exception accumulation stay identical.
        /// </summary>
        private static T DoWithIntervals<T>(Func<T> action, Func<int, TimeSpan> intervalForRetry, int retryCount)
        {
            var exceptions = new List<Exception>();

            for (int retry = 0; retry < retryCount; retry++)
            {
                try
                {
                    return action();
                }
                catch (Exception ex) when (IsRetryable(ex))
                {
                    exceptions.Add(ex);
                    // Don't sleep after the final attempt: there is no retry left to wait for.
                    if (retry < retryCount - 1)
                        Thread.Sleep(intervalForRetry(retry));
                }
            }

            throw new AggregateException(exceptions);
        }

        // Preserve the historical behaviour for TFS server errors and GitTfsException
        // (the latter also lets a wrapped MappingConflictException through), and additionally
        // retry the broader set of transient network faults classified by GitTfs core
        // (SocketException / IOException / TimeoutException / WebException, including wrapped ones).
        // Shared by every Retry overload so the retry classification stays identical across them.
        private static bool IsRetryable(Exception ex)
            => ex is Microsoft.TeamFoundation.TeamFoundationServerException
               || ex is GitTfsException
               || RetryPolicy.IsTransient(ex);

        public static void DoWhile(Func<bool> action, int retryCount = 10) => DoWhile(action, TimeSpan.FromSeconds(0), retryCount);

        public static void DoWhile(Func<bool> action, TimeSpan retryInterval, int retryCount = 10)
        {
            int count = 0;
            while (action())
            {
                count++;
                if (count > retryCount)
                    throw new GitTfsException("error: Action failed after " + retryCount + " retries!");
                Thread.Sleep(retryInterval);
            }
        }
    }
}
