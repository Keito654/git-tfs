using System.Net;
using System.Net.Sockets;

using GitTfs.Core;

using Xunit;

namespace GitTfs.Test.Core
{
    public class RetryPolicyTests : BaseTest
    {
        [Fact]
        public void WebExceptionIsTransient()
        {
            Assert.True(RetryPolicy.IsTransient(new WebException()));
        }

        [Fact]
        public void SocketExceptionIsTransient()
        {
            Assert.True(RetryPolicy.IsTransient(new SocketException()));
        }

        [Fact]
        public void IOExceptionIsTransient()
        {
            Assert.True(RetryPolicy.IsTransient(new IOException()));
        }

        [Fact]
        public void TimeoutExceptionIsTransient()
        {
            Assert.True(RetryPolicy.IsTransient(new TimeoutException()));
        }

        [Fact]
        public void PlainExceptionIsNotTransient()
        {
            Assert.False(RetryPolicy.IsTransient(new Exception("boom")));
        }

        [Fact]
        public void ArgumentExceptionIsNotTransient()
        {
            Assert.False(RetryPolicy.IsTransient(new ArgumentException("bad arg")));
        }

        [Fact]
        public void WrappedTransientExceptionIsTransient()
        {
            var wrapped = new Exception("wrap", new SocketException());
            Assert.True(RetryPolicy.IsTransient(wrapped));
        }

        [Fact]
        public void DeeplyNestedTransientExceptionIsTransient()
        {
            var wrapped = new Exception("outer", new InvalidOperationException("middle", new IOException()));
            Assert.True(RetryPolicy.IsTransient(wrapped));
        }

        [Fact]
        public void WrappedNonTransientExceptionIsNotTransient()
        {
            var wrapped = new Exception("outer", new ArgumentException("inner"));
            Assert.False(RetryPolicy.IsTransient(wrapped));
        }

        [Fact]
        public void NonTransientChainTerminatingInNullDoesNotLoop()
        {
            // A multi-level non-transient chain ends in InnerException == null; the walk
            // must terminate and return false rather than looping.
            var chain = new Exception("outer", new InvalidOperationException("middle", new ArgumentException("inner")));
            Assert.False(RetryPolicy.IsTransient(chain));
        }

        [Fact]
        public void BackoffIntervalsDoubleEachStepUntilCapped()
        {
            var intervals = RetryPolicy.BackoffIntervals(TimeSpan.FromSeconds(2), 5, TimeSpan.FromSeconds(30));

            Assert.Equal(new[]
            {
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(16),
                TimeSpan.FromSeconds(30), // 32s doubled would exceed the 30s cap
            }, intervals);
        }

        [Fact]
        public void BackoffIntervalsLengthMatchesCount()
        {
            var intervals = RetryPolicy.BackoffIntervals(TimeSpan.FromSeconds(1), 8, TimeSpan.FromSeconds(10));

            Assert.Equal(8, intervals.Count);
        }

        [Fact]
        public void BackoffIntervalsPlateauAtCap()
        {
            var intervals = RetryPolicy.BackoffIntervals(TimeSpan.FromSeconds(1), 6, TimeSpan.FromSeconds(4));

            // 1, 2, 4, then capped at 4 for every subsequent interval.
            Assert.Equal(new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(4),
            }, intervals);
        }

        [Fact]
        public void BackoffIntervalsCapAppliesToFirstIntervalWhenInitialExceedsCap()
        {
            var intervals = RetryPolicy.BackoffIntervals(TimeSpan.FromSeconds(50), 3, TimeSpan.FromSeconds(30));

            Assert.Equal(new[]
            {
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
            }, intervals);
        }

        [Fact]
        public void BackoffIntervalsWithZeroCountIsEmpty()
        {
            var intervals = RetryPolicy.BackoffIntervals(TimeSpan.FromSeconds(2), 0, TimeSpan.FromSeconds(30));

            Assert.Empty(intervals);
        }
    }
}
