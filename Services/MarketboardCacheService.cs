using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;

namespace AuxMarketboard.Services;

/// <summary>
/// Local, incrementally-updated cache of marketboard listings per item.
///
/// Universalis is crowd-sourced: a listing only appears/updates there once *someone's*
/// game client views that item's listings and uploads them, so results can lag reality by
/// anywhere from seconds to hours, and successive polls can flicker a listing in and out
/// (e.g. one poll catches it mid-upload, the next doesn't) even though nothing actually
/// changed on the real marketboard. To smooth that out:
///   - Listings are merged by their stable Universalis listing ID instead of being replaced
///     wholesale on every poll. A listing that goes briefly missing from one poll is kept
///     around (using its last known data) for a short grace period before being dropped, so a
///     single flaky poll can't make an entry disappear-then-reappear in the UI.
///   - Whenever the player's own client actually opens the marketboard and views an item in
///     game (<see cref="IMarketBoard.OfferingsReceived"/>), that is ground-truth, "as-of-right-now"
///     data straight from the server - more current than anything Universalis can offer. It fully
///     replaces the cached listings for that item and is flagged so the UI can show it was a
///     live, in-person look rather than a crowd-sourced snapshot.
/// </summary>
public sealed class MarketboardCacheService : IDisposable
{
    private static readonly TimeSpan MaxListingAge = TimeSpan.FromMinutes(30);
    private const int MissTolerance = 2;

    private readonly IMarketBoard marketBoard;
    private readonly IPluginLog log;
    private readonly object sync = new();
    private readonly Dictionary<uint, ItemCache> cache = new();

    public MarketboardCacheService(IMarketBoard marketBoard, IPluginLog log)
    {
        this.marketBoard = marketBoard;
        this.log = log;
        this.marketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    /// <summary>
    /// Raised when the cache for an item changes as a result of the player's client actually
    /// viewing that item on the marketboard in-game (not from a Universalis poll). The UI can
    /// use this to refresh that single item's displayed listings immediately, without waiting
    /// for the next scheduled Universalis refresh.
    /// </summary>
    public event Action<uint>? ItemCacheUpdatedFromLiveView;

    /// <summary>
    /// Merges a freshly-polled Universalis response into the local cache for one item,
    /// returning the resulting smoothed/merged listings (cheapest-first) to use for display.
    /// </summary>
    public IReadOnlyList<RecentPriceDetail> MergeUniversalisListings(uint itemId, IReadOnlyList<RecentPriceDetail> freshListings)
    {
        var now = DateTime.UtcNow;
        lock (sync)
        {
            var itemCache = GetOrCreate(itemId);
            var seenKeys = new HashSet<string>();

            foreach (var listing in freshListings)
            {
                var key = BuildKey(listing);
                seenKeys.Add(key);

                if (itemCache.Listings.TryGetValue(key, out var existing))
                {
                    existing.Detail = listing;
                    existing.MissStreak = 0;
                    existing.LastSeenUtc = now;
                }
                else
                {
                    itemCache.Listings[key] = new CachedListing
                    {
                        Detail = listing,
                        LastSeenUtc = now,
                    };
                }
            }

            foreach (var (key, cached) in itemCache.Listings.ToList())
            {
                if (seenKeys.Contains(key))
                {
                    continue;
                }

                cached.MissStreak++;
                var age = now - cached.LastSeenUtc;
                if (cached.MissStreak > MissTolerance || age > MaxListingAge)
                {
                    itemCache.Listings.Remove(key);
                }
            }

            return SnapshotLocked(itemCache, int.MaxValue);
        }
    }

    /// <summary>
    /// Returns the current cached listings for an item (cheapest-first), up to <paramref name="take"/> entries.
    /// </summary>
    public IReadOnlyList<RecentPriceDetail> GetCachedListings(uint itemId, int take)
    {
        lock (sync)
        {
            if (!cache.TryGetValue(itemId, out var itemCache))
            {
                return Array.Empty<RecentPriceDetail>();
            }

            return SnapshotLocked(itemCache, take);
        }
    }

    /// <summary>
    /// True if the given item's cache was last fully refreshed by the player actually viewing
    /// it in-game (rather than a Universalis poll), and that view happened recently.
    /// </summary>
    public bool WasRecentlyViewedLive(uint itemId, TimeSpan within)
    {
        lock (sync)
        {
            return cache.TryGetValue(itemId, out var itemCache)
                && itemCache.LastLiveViewUtc != DateTime.MinValue
                && DateTime.UtcNow - itemCache.LastLiveViewUtc <= within;
        }
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        try
        {
            if (offerings.ItemListings.Count == 0)
            {
                return;
            }

            var itemId = offerings.ItemListings[0].ItemId;
            var worldName = ResolveCurrentWorldName();
            var now = DateTime.UtcNow;

            lock (sync)
            {
                var itemCache = GetOrCreate(itemId);
                itemCache.Listings.Clear();

                foreach (var listing in offerings.ItemListings)
                {
                    var detail = new RecentPriceDetail
                    {
                        UnitPrice = listing.PricePerUnit,
                        Quantity = Math.Max(1, (int)listing.ItemQuantity),
                        WorldName = worldName,
                        UnixTimestamp = ((DateTimeOffset)now).ToUnixTimeSeconds(),
                        ListingId = listing.ListingId,
                    };

                    var key = BuildKey(detail);
                    itemCache.Listings[key] = new CachedListing
                    {
                        Detail = detail,
                        LastSeenUtc = now,
                    };
                }

                itemCache.LastLiveViewUtc = now;
            }

            ItemCacheUpdatedFromLiveView?.Invoke(itemId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to process live marketboard offerings.");
        }
    }

    private static string ResolveCurrentWorldName()
    {
        try
        {
            return Plugin.PlayerState.IsLoaded && Plugin.PlayerState.CurrentWorld.IsValid
                ? Plugin.PlayerState.CurrentWorld.Value.Name.ToString()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<RecentPriceDetail> SnapshotLocked(ItemCache itemCache, int take)
    {
        return itemCache.Listings.Values
            .OrderBy(x => x.Detail.UnitPrice)
            .ThenByDescending(x => x.LastSeenUtc)
            .Take(take)
            .Select(x => x.Detail)
            .ToList();
    }

    private ItemCache GetOrCreate(uint itemId)
    {
        if (!cache.TryGetValue(itemId, out var itemCache))
        {
            itemCache = new ItemCache();
            cache[itemId] = itemCache;
        }

        return itemCache;
    }

    private static string BuildKey(RecentPriceDetail listing)
    {
        // Prefer the stable Universalis/game listing ID. Fall back to a composite key for the
        // rare case we only have historical-sale data (no listing ID), so repeated merges don't
        // pile up unbounded duplicate "unknown" entries.
        return listing.ListingId != 0
            ? $"id:{listing.ListingId}"
            : $"w:{listing.WorldName}|p:{listing.UnitPrice}|q:{listing.Quantity}";
    }

    public void Dispose()
    {
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
    }

    private sealed class ItemCache
    {
        public readonly Dictionary<string, CachedListing> Listings = new();
        public DateTime LastLiveViewUtc = DateTime.MinValue;
    }

    private sealed class CachedListing
    {
        public RecentPriceDetail Detail = new();
        public DateTime LastSeenUtc;
        public int MissStreak;
    }
}
