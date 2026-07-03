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
        {
            var exceptions = new List<Exception>();

            for (int retry = 0; retry < retryCount; retry++)
            {
                try
                {
                    return action();
                }
                // Preserve the historical behaviour for TFS server errors and GitTfsException
                // (the latter also lets a wrapped MappingConflictException through), and additionally
                // retry the broader set of transient network faults classified by GitTfs core
                // (SocketException / IOException / TimeoutException / WebException, including wrapped ones).
                catch (Exception ex) when (ex is Microsoft.TeamFoundation.TeamFoundationServerException
                                           || ex is GitTfsException
                                           || RetryPolicy.IsTransient(ex))
                {
                    exceptions.Add(ex);
                    // Don't sleep after the final attempt: there is no retry left to wait for.
                    if (retry < retryCount - 1)
                        Thread.Sleep(retryInterval);
                }
            }

            throw new AggregateException(exceptions);
        }

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
