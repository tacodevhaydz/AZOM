using System;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Re-entrant guard for WPF event handlers. Settings panels need to
    /// programmatically update a slider/combo from the data model on a
    /// 500 ms refresh tick, but the resulting <c>ValueChanged</c> / <c>SelectionChanged</c>
    /// events look identical to a user edit. Each refresh wraps itself in
    /// a <see cref="Begin"/> scope and every event handler returns early
    /// when <see cref="Suppressed"/> is true.
    ///
    /// A depth counter (not a single bool) so nested refreshes don't release
    /// suppression prematurely. <see cref="Begin"/> returns an
    /// <see cref="IDisposable"/> so callers can <c>using (suppressor.Begin())</c>
    /// and let the framework handle exceptions / early returns.
    /// </summary>
    internal sealed class EventSuppressor
    {
        private int _depth;

        public bool Suppressed => _depth > 0;

        public IDisposable Begin() => new Scope(this);

        private sealed class Scope : IDisposable
        {
            private EventSuppressor? _owner;

            public Scope(EventSuppressor owner)
            {
                _owner = owner;
                owner._depth++;
            }

            public void Dispose()
            {
                var owner = _owner;
                if (owner == null) return;
                _owner = null;
                owner._depth--;
            }
        }
    }
}
