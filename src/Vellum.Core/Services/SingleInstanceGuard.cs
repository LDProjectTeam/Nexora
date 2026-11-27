using System;
using System.Threading;

namespace Vellum.Core.Services
{
    /// <summary>Cross-process guard used by the bootstrapper and by tests.</summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        private Mutex? _mutex;
        private bool _owned;

        private SingleInstanceGuard(Mutex mutex, bool owned)
        {
            _mutex = mutex;
            _owned = owned;
        }

        public static SingleInstanceGuard? TryAcquire(string name)
        {
            var mutex = new Mutex(false, name);
            try
            {
                var owned = mutex.WaitOne(TimeSpan.Zero);
                return owned ? new SingleInstanceGuard(mutex, true) : new SingleInstanceGuard(mutex, false);
            }
            catch (AbandonedMutexException)
            {
                return new SingleInstanceGuard(mutex, true);
            }
        }

        public bool Owned => _owned;

        /// <summary>True when another process currently owns the named mutex.</summary>
        public static bool IsHeldByAnother(string name)
        {
            Mutex? mutex = null;
            try
            {
                mutex = Mutex.OpenExisting(name);
                if (mutex.WaitOne(TimeSpan.Zero))
                {
                    mutex.ReleaseMutex();
                    return false;
                }
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (AbandonedMutexException)
            {
                try { mutex?.ReleaseMutex(); } catch { }
                return false;
            }
            finally
            {
                mutex?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_mutex == null)
                return;
            if (_owned)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _owned = false;
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
