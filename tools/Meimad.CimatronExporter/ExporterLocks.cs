namespace Meimad.CimatronExporter;

/// <summary>
/// Named locks of the current Windows session. <see cref="Export"/> is held while a package is exported: the exporter posts
/// the toolpath again through Cimatron, which runs the post-processor and its Meimad hook, and a hook call that finds the
/// lock held is the exporter's own post and must not start another export. <see cref="Window"/> is held while the exporter
/// window is open, so a second start (toolbar button, shortcut or post) does not open a second window.
/// </summary>
internal static class ExporterLocks
{
    public const string Export = @"Local\Meimad.CimatronExporter.Export";
    public const string Window = @"Local\Meimad.CimatronExporter.Window";

    /// <summary>Takes the lock on the calling thread, or null when another thread or process holds it.</summary>
    public static HeldLock? TryTake(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(0)) return new HeldLock(mutex);
        }
        catch (AbandonedMutexException)
        {
            // The holder ended without releasing it: the lock is now ours.
            return new HeldLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public static bool IsHeld(string name)
    {
        using var probe = TryTake(name);
        return probe is null;
    }

    /// <summary>Releases the lock on dispose; dispose it on the thread that took it.</summary>
    internal sealed class HeldLock(Mutex mutex) : IDisposable
    {
        private bool released;

        public void Dispose()
        {
            if (released) return;
            released = true;
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread any more.
            }

            mutex.Dispose();
        }
    }
}
