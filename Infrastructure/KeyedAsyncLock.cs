using System.Collections.Concurrent;

namespace SennheiserTray
{
    public sealed class KeyedAsyncLock<TKey> where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _semaphores = new();

        public async Task<IDisposable> AcquireAsync(
            TKey key,
            CancellationToken cancellationToken = default)
        {
            SemaphoreSlim semaphore = _semaphores.GetOrAdd(
                key,
                static _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(semaphore);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim? _semaphore;

            public Releaser(SemaphoreSlim semaphore)
            {
                _semaphore = semaphore;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _semaphore, null)?.Release();
            }
        }
    }
}
