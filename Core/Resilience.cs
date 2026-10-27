using System.Collections.Concurrent;

namespace DexScannerBot.Core
{
    public enum CircuitState { Closed, Open, HalfOpen }

    public class CircuitBreaker
    {
        private readonly int _threshold;
        private readonly TimeSpan _retryAfter;
        private int _failures;
        private CircuitState _state = CircuitState.Closed;
        private DateTime _lastFailure = DateTime.MinValue;
        private readonly object _lock = new();

        public CircuitBreaker(int threshold = 5, int retryAfterSeconds = 30)
        {
            _threshold = threshold;
            _retryAfter = TimeSpan.FromSeconds(retryAfterSeconds);
        }

        public bool CanProceed
        {
            get
            {
                lock (_lock)
                {
                    if (_state == CircuitState.Closed) return true;
                    if (_state == CircuitState.HalfOpen) return true;
                    if (DateTime.UtcNow - _lastFailure > _retryAfter)
                    {
                        _state = CircuitState.HalfOpen;
                        return true;
                    }
                    return false;
                }
            }
        }

        public void RecordSuccess()
        {
            lock (_lock)
            {
                _failures = 0;
                _state = CircuitState.Closed;
            }
        }

        public void RecordFailure()
        {
            lock (_lock)
            {
                _failures++;
                _lastFailure = DateTime.UtcNow;
                if (_failures >= _threshold)
                {
                    _state = CircuitState.Open;
                }
            }
        }

        public CircuitState State
        {
            get
            {
                lock (_lock) return _state;
            }
        }
    }

    public class RateLimiter
    {
        private readonly int _maxTokens;
        private readonly double _refillRatePerMs;
        private double _tokens;
        private DateTime _lastRefill;
        private readonly object _lock = new();

        public RateLimiter(int maxTokens, int refillPeriodMs)
        {
            _maxTokens = maxTokens;
            _refillRatePerMs = (double)maxTokens / refillPeriodMs;
            _tokens = maxTokens;
            _lastRefill = DateTime.UtcNow;
        }

        public bool TryAcquire(int tokens = 1)
        {
            lock (_lock)
            {
                Refill();
                if (_tokens >= tokens)
                {
                    _tokens -= tokens;
                    return true;
                }
                return false;
            }
        }

        private void Refill()
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRefill).TotalMilliseconds;
            _tokens = Math.Min(_maxTokens, _tokens + elapsed * _refillRatePerMs);
            _lastRefill = now;
        }

        public double AvailableTokens
        {
            get
            {
                lock (_lock) { Refill(); return _tokens; }
            }
        }
    }

    public static class RetryPolicy
    {
        public static async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            int maxRetries = 3,
            int baseDelayMs = 200,
            CircuitBreaker? circuitBreaker = null,
            CancellationToken ct = default)
        {
            if (circuitBreaker != null && !circuitBreaker.CanProceed)
                throw new InvalidOperationException("Circuit breaker is open");

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var result = await operation();
                    circuitBreaker?.RecordSuccess();
                    return result;
                }
                catch (Exception)
                {
                    circuitBreaker?.RecordFailure();
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
            }

            try { return await operation(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Operation failed after {maxRetries} retries", ex);
            }
        }

        public static async Task ExecuteAsync(
            Func<Task> operation,
            int maxRetries = 3,
            int baseDelayMs = 200,
            CircuitBreaker? circuitBreaker = null,
            CancellationToken ct = default)
        {
            await ExecuteAsync(async () =>
            {
                await operation();
                return true;
            }, maxRetries, baseDelayMs, circuitBreaker, ct);
        }
    }
}
