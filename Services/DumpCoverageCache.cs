using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Keeps what the dump's coverage screen computes (the entries linked to the library, the summaries, the series groups) so that a
    /// click does not redo it. A result stays valid for the SAME downloaded copy of the dump (a reload, or a change of its settings,
    /// replaces it), the same mapping/settings, and as long as the library did not change: Jellyfin's item events bump a version
    /// counter, a safety expiry covers a missed event. Log tag <c>📦 [DUMP]</c>.
    /// </summary>
    public sealed class DumpCoverageCache : IHostedService
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

        private readonly ILibraryManager _library;
        private readonly ILogger<DumpCoverageCache> _logger;
        private readonly ConcurrentDictionary<string, Slot> _slots = new ConcurrentDictionary<string, Slot>();
        private readonly ConcurrentDictionary<string, object> _locks = new ConcurrentDictionary<string, object>();
        private long _version;

        private sealed class Slot
        {
            public DumpCatalog Catalog;
            public string Key;
            public long Version;
            public DateTime BuiltUtc;
            public object Value;
        }

        public DumpCoverageCache(ILibraryManager library, ILogger<DumpCoverageCache> logger)
        {
            _library = library;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _library.ItemAdded += OnLibraryChanged;
            _library.ItemUpdated += OnLibraryChanged;
            _library.ItemRemoved += OnLibraryChanged;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _library.ItemAdded -= OnLibraryChanged;
            _library.ItemUpdated -= OnLibraryChanged;
            _library.ItemRemoved -= OnLibraryChanged;
            return Task.CompletedTask;
        }

        private void OnLibraryChanged(object sender, ItemChangeEventArgs e)
        {
            // Fires for every item of the library: only what the coverage reads (files, series, seasons) matters
            if (e?.Item != null && !e.Item.IsVirtualItem && (e.Item is Episode || e.Item is Movie || e.Item is Series || e.Item is Season))
            {
                Invalidate();
            }
        }

        /// <summary>The library changed: the results that depend on it are computed again at their next use</summary>
        public void Invalidate()
        {
            Interlocked.Increment(ref _version);
        }

        /// <summary>
        /// The kept result of <paramref name="slotName"/> when it was computed for this very <paramref name="catalog"/> copy and this
        /// <paramref name="key"/> (and, when <paramref name="libraryDependent"/>, with the library as it still is); otherwise computed
        /// by <paramref name="build"/>, once even when several requests arrive together. The result is shared: never modify it.
        /// </summary>
        public T GetOrBuild<T>(string slotName, DumpCatalog catalog, string key, Func<T> build, bool libraryDependent = true)
            where T : class
        {
            if (TryGet(slotName, catalog, key, libraryDependent, out T hit))
            {
                return hit;
            }

            lock (_locks.GetOrAdd(slotName, _ => new object()))
            {
                if (TryGet(slotName, catalog, key, libraryDependent, out hit))
                {
                    return hit;
                }

                var version = Interlocked.Read(ref _version);
                var watch = Stopwatch.StartNew();
                var value = build();
                _slots[slotName] = new Slot { Catalog = catalog, Key = key, Version = version, BuiltUtc = DateTime.UtcNow, Value = value };
                _logger.LogInformation("📦 [DUMP] Coverage '{Slot}' computed in {Ms} ms (kept until the dump, its settings or the library change)", slotName, watch.ElapsedMilliseconds);
                return value;
            }
        }

        private bool TryGet<T>(string slotName, DumpCatalog catalog, string key, bool libraryDependent, out T value)
            where T : class
        {
            value = null;
            if (!_slots.TryGetValue(slotName, out var slot))
            {
                return false;
            }

            if (!ReferenceEquals(slot.Catalog, catalog) || slot.Key != key
                || (libraryDependent && (slot.Version != Interlocked.Read(ref _version) || DateTime.UtcNow - slot.BuiltUtc > MaxAge)))
            {
                return false;
            }

            value = slot.Value as T;
            return value != null;
        }
    }
}
