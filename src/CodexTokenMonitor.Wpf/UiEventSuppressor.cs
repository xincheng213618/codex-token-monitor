using System.Threading;

namespace CodexTokenMonitor;

/// <summary>
/// Counts in-flight programmatic control updates so WPF change handlers can
/// distinguish user actions from values the window itself assigned. Scopes are
/// nestable and release even when the guarded block throws, replacing per-flag
/// booleans that could otherwise stay set after a failed update.
/// </summary>
internal sealed class UiEventSuppressor
{
    private int depth;

    public bool IsSuppressing => Volatile.Read(ref depth) > 0;

    public Scope Begin()
    {
        Interlocked.Increment(ref depth);
        return new Scope(this);
    }

    public sealed class Scope : IDisposable
    {
        private UiEventSuppressor? owner;

        internal Scope(UiEventSuppressor owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            // A double dispose or a stray scope must not drive the depth
            // negative and unmask programmatic updates to event handlers.
            var currentOwner = Interlocked.Exchange(ref owner, null);
            if (currentOwner is null)
            {
                return;
            }

            Interlocked.Decrement(ref currentOwner.depth);
        }
    }
}
