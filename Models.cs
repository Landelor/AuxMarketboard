using System.Text.Json.Serialization;

namespace AuxMarketboard;

public sealed class ListEntry
{
    public uint ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public List<RecentPriceDetail> RecentPrices { get; set; } = new();
    public long FallbackUnitPrice { get; set; }
    public string Status { get; set; } = "Not queried";

    /// <summary>
    /// Builds the cheapest way to acquire <see cref="Quantity"/> units of this item from the
    /// available listings (assumed sorted cheapest-first), buying whole listings before moving
    /// on to the next cheapest one, and only taking a partial amount from the last listing used.
    /// If the available listings don't cover the full quantity, the shortfall is represented as
    /// a single fallback entry.
    /// </summary>
    public List<FulfillmentLine> BuildFulfillmentPlan()
    {
        var plan = new List<FulfillmentLine>();
        var remaining = Quantity;

        foreach (var listing in RecentPrices)
        {
            if (remaining <= 0)
            {
                break;
            }

            var available = Math.Max(1, listing.Quantity);
            var take = Math.Min(remaining, available);
            plan.Add(new FulfillmentLine(listing.WorldName, listing.UnitPrice, take, false));
            remaining -= take;
        }

        if (remaining > 0 && FallbackUnitPrice > 0)
        {
            plan.Add(new FulfillmentLine("Fallback/Unknown", FallbackUnitPrice, remaining, true));
            remaining = 0;
        }

        return plan;
    }

    public long EstimatedTotal => BuildFulfillmentPlan().Sum(x => x.UnitPrice * x.QuantityUsed);
}

public readonly record struct FulfillmentLine(string WorldName, long UnitPrice, int QuantityUsed, bool IsFallback);

public sealed class RecentPriceDetail
{
    public long UnitPrice { get; set; }
    public int Quantity { get; set; } = 1;
    public string WorldName { get; set; } = string.Empty;
    public long UnixTimestamp { get; set; }
}

public sealed class UniversalisMultiResponse
{
    [JsonPropertyName("itemIDs")]
    public List<uint>? ItemIds { get; set; }

    [JsonPropertyName("items")]
    public Dictionary<string, UniversalisItemResponse>? Items { get; set; }

    [JsonPropertyName("unresolvedItems")]
    public List<uint>? UnresolvedItems { get; set; }
}

public sealed class UniversalisItemResponse
{
    [JsonPropertyName("listings")]
    public List<UniversalisListing>? Listings { get; set; }

    [JsonPropertyName("recentHistory")]
    public List<UniversalisHistoryEntry>? RecentHistory { get; set; }

    [JsonPropertyName("minPrice")]
    public int MinPrice { get; set; }

    [JsonPropertyName("hasData")]
    public bool HasData { get; set; }

    [JsonPropertyName("lastUploadTime")]
    public long LastUploadTime { get; set; }
}

public sealed class UniversalisListing
{
    [JsonPropertyName("pricePerUnit")]
    public int PricePerUnit { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; } = 1;

    [JsonPropertyName("lastReviewTime")]
    public long LastReviewTime { get; set; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; set; }
}

public sealed class UniversalisHistoryEntry
{
    [JsonPropertyName("pricePerUnit")]
    public int PricePerUnit { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; } = 1;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; set; }
}

public sealed class ArtisanExport
{
    public List<ArtisanRecipe>? Recipes { get; set; }
    public List<uint>? Items { get; set; }
}

public sealed class ArtisanRecipe
{
    public uint ID { get; set; }
    public int Quantity { get; set; }
}
