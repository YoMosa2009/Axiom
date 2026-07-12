using System;

namespace Malx_AI
{
    internal sealed class OpenRouterUsageRefreshThrottle
    {
        private readonly object _gate = new();
        private readonly TimeSpan _minimumInterval;
        private DateTime _lastStartedUtc = DateTime.MinValue;
        private bool _isRunning;

        public OpenRouterUsageRefreshThrottle(TimeSpan minimumInterval)
        {
            _minimumInterval = minimumInterval;
        }

        public bool TryBegin(DateTime utcNow, bool hasValidKey)
        {
            if (!hasValidKey)
                return false;

            lock (_gate)
            {
                if (_isRunning || utcNow - _lastStartedUtc < _minimumInterval)
                    return false;

                _isRunning = true;
                _lastStartedUtc = utcNow;
                return true;
            }
        }

        public void Complete()
        {
            lock (_gate)
                _isRunning = false;
        }
    }
}
