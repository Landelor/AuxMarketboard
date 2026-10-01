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

    public long EstimatedTotal
    {
        get
        {
            if (RecentPrices.Count == 0 && FallbackUnitPrice <= 0)
            {
                return 0;
            }

            var prices = RecentPrices.Take(Quantity).Select(x => x.UnitPrice).ToList();
            while (prices.Count < Quantity)
            {
                prices.Add(FallbackUnitPrice);
            }

            return prices.Sum();
        }
    }
}

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
