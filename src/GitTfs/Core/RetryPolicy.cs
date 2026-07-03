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
    }
}
