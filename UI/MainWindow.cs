using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using AuxMarketboard.Services;

namespace AuxMarketboard.UI;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly UniversalisClient universalisClient;
    private readonly ItemResolver itemResolver;
    private readonly object syncRoot = new();
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly List<ListEntry> entries = new();
    private readonly HashSet<uint> refreshingItemIds = new();
    private CancellationTokenSource? refreshCts;
    private Task? refreshTask;

    private string itemSearch = string.Empty;
    private uint selectedItemId;
    private string selectedItemName = "Select item";
    private int addQuantity = 1;
    private string importText = string.Empty;
    private string statusText = "Ready.";
    private DateTime nextAutoRefreshUtc = DateTime.UtcNow;
    private DateTime lastRefreshUtc = DateTime.MinValue;
    private DateTime currentRefreshStartedUtc = DateTime.MinValue;
    private readonly HashSet<uint> expandedItemIds = new();
    private readonly HashSet<string> expandedBuyServers = new(StringComparer.OrdinalIgnoreCase);
    private int lastUpdatedItemCount;

    public MainWindow(Configuration configuration, UniversalisClient universalisClient, ItemResolver itemResolver)
        : base("AuxMarketboard")
    {
        this.configuration = configuration;
        this.universalisClient = universalisClient;
        this.itemResolver = itemResolver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(760, 520),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
        refreshCts?.Cancel();
        refreshCts?.Dispose();
    }

    public override void Draw()
    {
        HandleAutoRefresh();
        DrawHeaderControls();
        ImGui.Separator();
        DrawEntrySections();
        ImGui.Separator();
        DrawResults();
    }

    private void DrawHeaderControls()
    {
        var world = configuration.WorldOrDataCenter;
        if (ImGui.InputText("World / DC / Region", ref world, 64))
        {
            configuration.WorldOrDataCenter = world;
            configuration.Save();
        }

        var autoDetect = configuration.AutoDetectWorldOnStartup;
        if (ImGui.Checkbox("Auto detect current world", ref autoDetect))
        {
            configuration.AutoDetectWorldOnStartup = autoDetect;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Use Current Home World"))
        {
            var detected = Plugin.PlayerState.IsLoaded && Plugin.PlayerState.HomeWorld.IsValid
                ? Plugin.PlayerState.HomeWorld.Value.Name.ToString()
                : null;
            if (!string.IsNullOrWhiteSpace(detected))
            {
                configuration.WorldOrDataCenter = detected;
                configuration.Save();
                statusText = $"Set world to {detected}.";
            }
            else
            {
                statusText = "No logged-in player world detected.";
            }
        }

        var includeNq = configuration.IncludeNormalQuality;
        if (ImGui.Checkbox("Include NQ", ref includeNq))
        {
            configuration.IncludeNormalQuality = includeNq;
            configuration.Save();
        }

        ImGui.SameLine();
        var includeHq = configuration.IncludeHighQuality;
        if (ImGui.Checkbox("Include HQ", ref includeHq))
        {
            configuration.IncludeHighQuality = includeHq;
            configuration.Save();
        }

        ImGui.SameLine();
        var groupedMode = configuration.GroupedMode;
        if (ImGui.Checkbox("Grouped mode", ref groupedMode))
        {
            configuration.GroupedMode = groupedMode;
            configuration.Save();
        }

        var searchRegion = configuration.SearchByRegion;
        if (ImGui.Checkbox("Search region", ref searchRegion))
        {
            configuration.SearchByRegion = searchRegion;
            if (searchRegion)
            {
                configuration.SearchByDataCenter = false;
            }
            else if (!configuration.SearchByDataCenter)
            {
                configuration.SearchByRegion = true;
            }
            configuration.Save();
        }

        ImGui.SameLine();
        var searchDc = configuration.SearchByDataCenter;
        if (ImGui.Checkbox("Search DC", ref searchDc))
        {
            configuration.SearchByDataCenter = searchDc;
            if (searchDc)
            {
                configuration.SearchByRegion = false;
            }
            else if (!configuration.SearchByRegion)
            {
                configuration.SearchByDataCenter = true;
            }
            configuration.Save();
        }

        var scopeToken = ResolveScopeTarget();
        ImGui.TextUnformatted($"Active scope: {scopeToken}");

        var autoRefresh = configuration.AutoRefreshEnabled;
        if (ImGui.Checkbox("Auto refresh", ref autoRefresh))
        {
            configuration.AutoRefreshEnabled = autoRefresh;
            nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(Math.Max(10, configuration.AutoRefreshIntervalSeconds));
            configuration.Save();
        }

        ImGui.SameLine();
        var interval = configuration.AutoRefreshIntervalSeconds;
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Interval (s)", ref interval))
        {
            configuration.AutoRefreshIntervalSeconds = Math.Clamp(interval, 10, 3600);
            configuration.Save();
        }

        if (lastRefreshUtc > DateTime.MinValue)
        {
            ImGui.TextUnformatted(GetUpdatedAgoText());
        }
    }

    private void DrawEntrySections()
    {
        if (ImGui.CollapsingHeader("Manual add", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawManualAdd();
        }

        if (ImGui.CollapsingHeader("Import list", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawImportSection();
        }
    }

    private void DrawManualAdd()
    {
        ImGui.Text("Add item manually");
        ImGui.InputText("Search", ref itemSearch, 128);
        ImGui.TextUnformatted($"Selected: {selectedItemName}");

        var matches = itemResolver.GetSearchMatches(itemSearch, 25);
        if (matches.Count > 0)
        {
            if (ImGui.BeginListBox("##dynamicSearch", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, 120)))
            {
                foreach (var match in matches)
                {
                    var selected = match.Id == selectedItemId;
                    if (ImGui.Selectable($"{match.Name} ({match.Id})", selected))
                    {
                        selectedItemId = match.Id;
                        selectedItemName = match.Name;
                    }
                }

                ImGui.EndListBox();
            }
        }

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Quantity", ref addQuantity);
        if (addQuantity < 1)
        {
            addQuantity = 1;
        }

        if (ImGui.Button("Add Item"))
        {
            if (selectedItemId == 0)
            {
                statusText = "Pick an item first.";
            }
            else
            {
                AddOrUpdateEntry(selectedItemId, selectedItemName, addQuantity);
                statusText = $"Added {selectedItemName} x{addQuantity}.";
            }
        }
    }

    private void DrawImportSection()
    {
        ImGui.Text("Import list (supports artisan/teamcraft link, or text lines)");
        ImGui.InputText("##import", ref importText, 500_000);

        if (ImGui.Button("Import"))
        {
            ImportEntries(importText);
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear List"))
        {
            lock (syncRoot)
            {
                entries.Clear();
            }
            statusText = "List cleared.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Paste Clipboard"))
        {
            importText = ImGui.GetClipboardText();
        }

        ImGui.TextWrapped(GetStatusText());
    }

    private void DrawResults()
    {
        List<ListEntry> snapshot;
        HashSet<uint> refreshingSnapshot;
        lock (syncRoot)
        {
            snapshot = entries.Select(CloneEntry).ToList();
            refreshingSnapshot = refreshingItemIds.ToHashSet();
        }

        var total = snapshot.Sum(x => x.EstimatedTotal);

        uint? removeItemId = null;
        var quantityUpdates = new List<(uint ItemId, int Quantity)>();
        if (ImGui.CollapsingHeader("Item prices", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable("priceTable", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Qty");
                ImGui.TableSetupColumn("Subtotal");
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 48f);
                ImGui.TableSetupColumn("Actions");
                ImGui.TableHeadersRow();

                foreach (var entry in snapshot)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    var isExpanded = expandedItemIds.Contains(entry.ItemId);
                    var label = isExpanded ? $"v {entry.Name}" : $"> {entry.Name}";
                    if (ImGui.SmallButton($"{label}##item{entry.ItemId}"))
                    {
                        if (isExpanded)
                        {
                            expandedItemIds.Remove(entry.ItemId);
                        }
                        else
                        {
                            expandedItemIds.Add(entry.ItemId);
                        }
                    }
                    ImGui.TableSetColumnIndex(1);
                    var qty = entry.Quantity;
                    ImGui.SetNextItemWidth(80);
                    if (ImGui.InputInt($"##qty{entry.ItemId}", ref qty))
                    {
                        quantityUpdates.Add((entry.ItemId, Math.Max(1, qty)));
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"+##{entry.ItemId}"))
                    {
                        quantityUpdates.Add((entry.ItemId, entry.Quantity + 1));
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"-##{entry.ItemId}"))
                    {
                        quantityUpdates.Add((entry.ItemId, Math.Max(1, entry.Quantity - 1)));
                    }
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted($"{entry.EstimatedTotal:N0}");
                    ImGui.TableSetColumnIndex(3);
                    DrawStatusIcon(entry, refreshingSnapshot.Contains(entry.ItemId));
                    ImGui.TableSetColumnIndex(4);
                    if (ImGui.SmallButton($"Remove##{entry.ItemId}"))
                    {
                        removeItemId = entry.ItemId;
                    }

                    if (isExpanded)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextUnformatted(string.Empty);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(string.Empty);
                        ImGui.TableSetColumnIndex(2);
                        DrawRecentPriceDetails(entry, configuration.GroupedMode);
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Separator();
            ImGui.TextUnformatted($"Total: {total:N0} gil");
        }

        if (ImGui.CollapsingHeader("Buy mode", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBuyModeTable(snapshot);
        }

        foreach (var update in quantityUpdates)
        {
            UpdateQuantity(update.ItemId, update.Quantity);
        }

        if (removeItemId.HasValue)
        {
            RemoveEntry(removeItemId.Value);
        }
    }

    private void DrawBuyModeTable(List<ListEntry> snapshot)
    {
        var listings = new List<(string Server, uint ItemId, string ItemName, long UnitPrice, int ListingQuantity, bool IsFallback)>();
        foreach (var entry in snapshot)
        {
            var selected = entry.RecentPrices.Take(entry.Quantity).ToList();
            foreach (var price in selected)
            {
                var server = string.IsNullOrWhiteSpace(price.WorldName) ? "Unknown" : price.WorldName;
                listings.Add((server, entry.ItemId, entry.Name, price.UnitPrice, Math.Max(1, price.Quantity), false));
            }

            var missing = entry.Quantity - selected.Count;
            for (var i = 0; i < missing; i++)
            {
                listings.Add(("Fallback/Unknown", entry.ItemId, entry.Name, entry.FallbackUnitPrice, 1, true));
            }
        }

        var serverGroups = listings
            .GroupBy(x => x.Server)
            .Select(g => new
            {
                Server = g.Key,
                ServerTotal = g.Sum(x => x.UnitPrice),
                Rows = g
                    .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.UnitPrice)
                    .Select(x => new
                    {
                        x.ItemId,
                        x.ItemName,
                        x.UnitPrice,
                        x.ListingQuantity,
                        x.IsFallback,
                    })
                    .ToList(),
            })
            .OrderBy(x => x.ServerTotal)
            .ThenBy(x => x.Server, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (serverGroups.Count == 0)
        {
            ImGui.TextUnformatted("No priced item data to show yet.");
            return;
        }

        if (!ImGui.BeginTable("buyModeServerTable", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable))
        {
            return;
        }

        ImGui.TableSetupColumn("Server");
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Total");
        ImGui.TableHeadersRow();

        foreach (var serverGroup in serverGroups)
        {
            var isExpanded = expandedBuyServers.Contains(serverGroup.Server);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var label = isExpanded ? $"v {serverGroup.Server}" : $"> {serverGroup.Server}";
            if (ImGui.SmallButton($"{label}##buy-{serverGroup.Server}"))
            {
                if (isExpanded)
                {
                    expandedBuyServers.Remove(serverGroup.Server);
                }
                else
                {
                    expandedBuyServers.Add(serverGroup.Server);
                }
                isExpanded = !isExpanded;
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{serverGroup.Rows.Count} listings");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(serverGroup.Rows.Sum(x => x.ListingQuantity).ToString());
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted($"{serverGroup.ServerTotal:N0}");

            if (!isExpanded)
            {
                continue;
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(string.Empty);
            ImGui.TableSetColumnIndex(1);
            if (ImGui.BeginTable($"buyModeItems-{serverGroup.Server}", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Listing Qty");
                ImGui.TableSetupColumn("Unit Price");
                ImGui.TableHeadersRow();

                foreach (var row in serverGroup.Rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(row.IsFallback ? $"{row.ItemName} (est.)" : row.ItemName);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(row.ListingQuantity.ToString());
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted($"{row.UnitPrice:N0}");
                }

                ImGui.EndTable();
            }
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(string.Empty);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(string.Empty);
        }

        ImGui.EndTable();
    }

    private static void DrawRecentPriceDetails(ListEntry entry, bool groupedMode)
    {
        if (entry.RecentPrices.Count == 0)
        {
            ImGui.TextUnformatted("No recent price entries.");
            return;
        }

        var details = entry.RecentPrices.Take(entry.Quantity).ToList();
        if (groupedMode)
        {
            var grouped = details
                .GroupBy(x => string.IsNullOrWhiteSpace(x.WorldName) ? "Unknown" : x.WorldName)
                .Select(g => new
                {
                    Server = g.Key,
                    Count = g.Count(),
                    Total = g.Sum(x => x.UnitPrice),
                })
                .OrderBy(x => x.Total)
                .ThenByDescending(x => x.Count)
                .ThenBy(x => x.Server)
                .ToList();

            foreach (var group in grouped)
            {
                ImGui.TextUnformatted($"{group.Count}x {group.Server}");
            }

            return;
        }

        if (ImGui.BeginTable($"recentTable{entry.ItemId}", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("#");
            ImGui.TableSetupColumn("Price");
            ImGui.TableSetupColumn("Server");
            ImGui.TableHeadersRow();

            for (var i = 0; i < details.Count; i++)
            {
                var d = details[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted((i + 1).ToString());
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(d.UnitPrice.ToString("N0"));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(d.WorldName) ? "-" : d.WorldName);
            }

            ImGui.EndTable();
        }
    }

    private void StartRefresh()
    {
        if (refreshTask is { IsCompleted: false })
        {
            return;
        }

        refreshCts?.Cancel();
        refreshCts?.Dispose();
        refreshCts = new CancellationTokenSource();
        refreshTask = RefreshPricesAsync(refreshCts.Token);
    }

    private async Task RefreshPricesAsync(CancellationToken cancellationToken)
    {
        List<ListEntry> snapshot;
        currentRefreshStartedUtc = DateTime.UtcNow;
        lock (syncRoot)
        {
            snapshot = entries.Select(CloneEntry).ToList();
            refreshingItemIds.Clear();
            foreach (var itemId in snapshot.Select(x => x.ItemId).Distinct())
            {
                refreshingItemIds.Add(itemId);
            }
        }

        if (snapshot.Count == 0)
        {
            statusText = "List is empty.";
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.WorldOrDataCenter))
        {
            statusText = "Set world / data center / region first.";
            return;
        }

        if (!configuration.IncludeHighQuality && !configuration.IncludeNormalQuality)
        {
            statusText = "Enable HQ and/or NQ first.";
            return;
        }

        var maxQuantity = Math.Max(1, snapshot.Max(x => x.Quantity));
        statusText = "Fetching Universalis prices...";

        try
        {
            var response = await universalisClient.GetCurrentMarketDataAsync(
                ResolveScopeTarget(),
                snapshot.Select(x => x.ItemId).Distinct().ToList(),
                maxQuantity,
                configuration.IncludeNormalQuality,
                configuration.IncludeHighQuality,
                cancellationToken);

            lock (syncRoot)
            {
                foreach (var entry in entries)
                {
                    if (!response.TryGetValue(entry.ItemId, out var data) || !data.HasData)
                    {
                        entry.RecentPrices = new List<RecentPriceDetail>();
                        entry.FallbackUnitPrice = 0;
                        entry.Status = "No market data";
                        continue;
                    }

                    var listingPrices = data.Listings?
                        .OrderBy(x => x.PricePerUnit)
                        .ThenByDescending(x => x.LastReviewTime)
                        .Select(x => new RecentPriceDetail
                        {
                            UnitPrice = x.PricePerUnit,
                            Quantity = Math.Max(1, x.Quantity),
                            WorldName = x.WorldName ?? string.Empty,
                            UnixTimestamp = x.LastReviewTime,
                        })
                        .Where(x => x.UnitPrice > 0)
                        .Take(entry.Quantity)
                        .ToList() ?? new List<RecentPriceDetail>();

                    if (listingPrices.Count == 0)
                    {
                        listingPrices = data.RecentHistory?
                            .OrderBy(x => x.PricePerUnit)
                            .ThenByDescending(x => x.Timestamp)
                            .Select(x => new RecentPriceDetail
                            {
                                UnitPrice = x.PricePerUnit,
                                Quantity = Math.Max(1, x.Quantity),
                                WorldName = x.WorldName ?? string.Empty,
                                UnixTimestamp = x.Timestamp,
                            })
                            .Where(x => x.UnitPrice > 0)
                            .Take(entry.Quantity)
                            .ToList() ?? new List<RecentPriceDetail>();
                    }

                    entry.RecentPrices = listingPrices;
                    entry.FallbackUnitPrice = data.MinPrice > 0
                        ? data.MinPrice
                        : (listingPrices.Count > 0 ? listingPrices.Last().UnitPrice : 0);
                    entry.Status = listingPrices.Count > 0
                        ? $"Updated ({listingPrices.Count} recent)"
                        : "Updated (fallback only)";
                }
            }

            lastRefreshUtc = DateTime.Now;
            lastUpdatedItemCount = snapshot.Count;
            statusText = "Updated.";
            nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(Math.Max(10, configuration.AutoRefreshIntervalSeconds));
        }
        catch (OperationCanceledException)
        {
            statusText = "Refresh canceled.";
        }
        catch (Exception ex)
        {
            statusText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            var elapsed = DateTime.UtcNow - currentRefreshStartedUtc;
            var remaining = TimeSpan.FromMilliseconds(300) - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            lock (syncRoot)
            {
                refreshingItemIds.Clear();
            }
        }
    }

    private void AddOrUpdateEntry(uint itemId, string name, int quantity)
    {
        AddOrUpdateEntry(itemId, name, quantity, true);
    }

    private void AddOrUpdateEntry(uint itemId, string name, int quantity, bool triggerRefresh)
    {
        var changed = false;
        lock (syncRoot)
        {
            var existing = entries.FirstOrDefault(x => x.ItemId == itemId);
            if (existing is null)
            {
                entries.Add(new ListEntry
                {
                    ItemId = itemId,
                    Name = name,
                    Quantity = quantity,
                    Status = "Waiting for refresh",
                });
                changed = true;
            }
            else
            {
                existing.Quantity += quantity;
                existing.Status = "Quantity updated";
                changed = true;
            }
        }

        if (triggerRefresh && changed)
        {
            StartRefresh();
        }
    }

    private void UpdateQuantity(uint itemId, int quantity)
    {
        var changed = false;
        lock (syncRoot)
        {
            var existing = entries.FirstOrDefault(x => x.ItemId == itemId);
            if (existing is null)
            {
                return;
            }

            var clamped = Math.Max(1, quantity);
            if (existing.Quantity != clamped)
            {
                existing.Quantity = clamped;
                existing.Status = "Quantity updated";
                changed = true;
            }
        }

        if (changed)
        {
            StartRefresh();
        }
    }

    private void RemoveEntry(uint itemId)
    {
        lock (syncRoot)
        {
            entries.RemoveAll(x => x.ItemId == itemId);
        }
        expandedItemIds.Remove(itemId);

        statusText = "Removed item.";
    }

    private void ImportEntries(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            statusText = "Import text is empty.";
            return;
        }

        var importedCount = 0;
        var failedLines = new List<string>();

        if (TryImportArtisanJson(source, out _, out var artisanMessage))
        {
            statusText = artisanMessage;
            return;
        }

        if (TryImportTeamcraft(source, out var teamcraftImported, out var teamcraftMessage))
        {
            statusText = teamcraftMessage;
            if (teamcraftImported > 0)
            {
                StartRefresh();
            }
            return;
        }

        foreach (var rawLine in source.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseLine(line, out var key, out var quantity))
            {
                failedLines.Add(line);
                continue;
            }

            if (!itemResolver.TryResolveByText(key, out var itemId, out var itemName))
            {
                failedLines.Add(line);
                continue;
            }

            AddOrUpdateEntry(itemId, itemName, quantity, false);
            importedCount++;
        }

        var sb = new StringBuilder();
        sb.Append($"Imported {importedCount} item entries.");
        if (failedLines.Count > 0)
        {
            sb.Append($" Could not parse/resolve {failedLines.Count} lines.");
        }

        statusText = sb.ToString();
        if (importedCount > 0)
        {
            StartRefresh();
        }
    }

    private bool TryImportTeamcraft(string source, out int importedCount, out string message)
    {
        importedCount = 0;
        message = string.Empty;

        if (!TryExtractTeamcraftPayload(source, out var payload))
        {
            return false;
        }

        foreach (var entry in payload.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            if (!uint.TryParse(parts[0], out var itemId))
            {
                continue;
            }

            if (!int.TryParse(parts[2], out var quantity) || quantity <= 0)
            {
                continue;
            }

            var name = itemResolver.TryGetName(itemId, out var resolvedName)
                ? resolvedName
                : $"Item {itemId}";
            AddOrUpdateEntry(itemId, name, quantity, false);
            importedCount++;
        }

        message = importedCount > 0
            ? $"Imported {importedCount} entries from Teamcraft link."
            : "Teamcraft link detected, but no valid entries were found.";
        return true;
    }

    private static bool TryExtractTeamcraftPayload(string source, out string payload)
    {
        payload = string.Empty;
        var trimmed = source.Trim();
        if (!trimmed.Contains("ffxivteamcraft.com/import/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var marker = "/import/";
        var index = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var encoded = uri.AbsolutePath[(index + marker.Length)..].Trim('/');
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        payload = DecodeBase64ToUtf8(encoded);
        return !string.IsNullOrWhiteSpace(payload);
    }

    private static string DecodeBase64ToUtf8(string encoded)
    {
        try
        {
            var normalized = encoded.Replace('-', '+').Replace('_', '/');
            var pad = normalized.Length % 4;
            if (pad > 0)
            {
                normalized = normalized.PadRight(normalized.Length + (4 - pad), '=');
            }

            var bytes = Convert.FromBase64String(normalized);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool TryImportArtisanJson(string source, out int importedCount, out string message)
    {
        importedCount = 0;
        message = string.Empty;

        ArtisanExport? export;
        try
        {
            export = JsonSerializer.Deserialize<ArtisanExport>(source, jsonOptions);
        }
        catch
        {
            return false;
        }

        if (export is null || (export.Recipes is null && export.Items is null))
        {
            return false;
        }

        if (export.Recipes is not null)
        {
            foreach (var recipe in export.Recipes.Where(x => x.ID > 0 && x.Quantity > 0))
            {
                if (itemResolver.TryResolveRecipeToItem(recipe.ID, out var itemId, out var itemName, out var yield))
                {
                    AddOrUpdateEntry(itemId, itemName, recipe.Quantity * yield, false);
                    importedCount++;
                }
            }
        }

        if (export.Items is not null)
        {
            foreach (var group in export.Items.Where(x => x > 0).GroupBy(x => x))
            {
                if (itemResolver.TryGetName(group.Key, out var name))
                {
                    AddOrUpdateEntry(group.Key, name, group.Count(), false);
                    importedCount++;
                }
            }
        }

        message = importedCount > 0
            ? $"Imported {importedCount} entries from artisan/teamcraft link."
            : "artisan/teamcraft link parsed, but no valid entries were found.";
        if (importedCount > 0)
        {
            StartRefresh();
        }
        return true;
    }

    private void HandleAutoRefresh()
    {
        if (!configuration.AutoRefreshEnabled)
        {
            return;
        }

        if (refreshTask is { IsCompleted: false })
        {
            return;
        }

        if (DateTime.UtcNow < nextAutoRefreshUtc)
        {
            return;
        }

        StartRefresh();
    }

    private static bool TryParseLine(string line, out string itemKey, out int quantity)
    {
        itemKey = string.Empty;
        quantity = 1;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        line = StripBulletPrefix(line.Trim());

        var csv = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (csv.Length >= 2 && TryParsePositiveInt(csv[^1], out quantity))
        {
            itemKey = string.Join(",", csv.Take(csv.Length - 1)).Trim();
            quantity = Math.Max(1, quantity);
            return !string.IsNullOrWhiteSpace(itemKey);
        }

        var match = Regex.Match(line, @"^(?<item>.+?)\s*[xX×]\s*(?<qty>\d+)$");
        if (match.Success && TryParsePositiveInt(match.Groups["qty"].Value, out quantity))
        {
            itemKey = match.Groups["item"].Value.Trim();
            return !string.IsNullOrWhiteSpace(itemKey);
        }

        match = Regex.Match(line, @"^(?<qty>\d+)\s*[xX×]\s*(?<item>.+?)$");
        if (match.Success && TryParsePositiveInt(match.Groups["qty"].Value, out quantity))
        {
            itemKey = match.Groups["item"].Value.Trim();
            return !string.IsNullOrWhiteSpace(itemKey);
        }

        match = Regex.Match(line, @"^(?<item>.+?)\s*[:\-]\s*(?<qty>\d+)$");
        if (match.Success && TryParsePositiveInt(match.Groups["qty"].Value, out quantity))
        {
            itemKey = match.Groups["item"].Value.Trim();
            return !string.IsNullOrWhiteSpace(itemKey);
        }

        itemKey = line.Trim();
        quantity = 1;
        return !string.IsNullOrWhiteSpace(itemKey);
    }

    private static string StripBulletPrefix(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(line, @"^\s*(?:[-*•]\s+|\d+[.)]\s+)", string.Empty);
        return cleaned.Trim();
    }

    private static bool TryParsePositiveInt(string value, out int quantity)
    {
        quantity = 0;
        if (!long.TryParse(value.Trim(), out var parsed))
        {
            return false;
        }

        if (parsed <= 0)
        {
            return false;
        }

        quantity = parsed > int.MaxValue ? int.MaxValue : (int)parsed;
        return true;
    }

    private static ListEntry CloneEntry(ListEntry source)
    {
        return new ListEntry
        {
            ItemId = source.ItemId,
            Name = source.Name,
            Quantity = source.Quantity,
            RecentPrices = source.RecentPrices.Select(x => new RecentPriceDetail
            {
                UnitPrice = x.UnitPrice,
                Quantity = x.Quantity,
                WorldName = x.WorldName,
                UnixTimestamp = x.UnixTimestamp,
            }).ToList(),
            FallbackUnitPrice = source.FallbackUnitPrice,
            Status = source.Status,
        };
    }

    private static string GetUpdatingLabel()
    {
        var dots = new string('.', ((int)(ImGui.GetTime() * 3) % 3) + 1);
        return $"Updating{dots}";
    }

    private static void DrawStatusIcon(ListEntry entry, bool isUpdating)
    {
        if (isUpdating)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.35f, 0.65f, 1.0f, 1.0f));
            ImGui.TextUnformatted("●");
            ImGui.PopStyleColor();
            return;
        }

        var isUpdated = entry.Status.StartsWith("Updated", StringComparison.OrdinalIgnoreCase);
        if (isUpdated)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.25f, 0.85f, 0.35f, 1.0f));
            ImGui.TextUnformatted("✓");
            ImGui.PopStyleColor();
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.95f, 0.30f, 0.30f, 1.0f));
        ImGui.TextUnformatted("✗");
        ImGui.PopStyleColor();
    }

    private string ResolveScopeTarget()
    {
        if (configuration.SearchByDataCenter)
        {
            if (TryGetPlayerDataCenterName(out var dc) && IsSupportedDataCenter(dc))
            {
                return dc;
            }

            if (IsSupportedDataCenter(configuration.WorldOrDataCenter))
            {
                return configuration.WorldOrDataCenter.Trim();
            }
        }

        if (configuration.SearchByRegion)
        {
            if (TryGetPlayerDataCenterName(out var playerDc) && TryMapDataCenterToRegion(playerDc, out var regionFromDc))
            {
                return regionFromDc;
            }

            var normalizedConfigured = NormalizeUniversalisRegion(configuration.WorldOrDataCenter);
            if (IsSupportedRegion(normalizedConfigured))
            {
                return normalizedConfigured;
            }

            if (TryMapDataCenterToRegion(configuration.WorldOrDataCenter, out var regionFromConfigDc))
            {
                return regionFromConfigDc;
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration.WorldOrDataCenter))
        {
            return configuration.WorldOrDataCenter.Trim();
        }

        return "Oceania";
    }

    private bool TryGetPlayerDataCenterName(out string dcName)
    {
        dcName = string.Empty;
        if (!Plugin.PlayerState.IsLoaded || !Plugin.PlayerState.CurrentWorld.IsValid)
        {
            return false;
        }

        var world = Plugin.PlayerState.CurrentWorld.Value;
        var dcRef = world.GetType().GetProperty("DataCenter")?.GetValue(world);
        var name = TryGetRowRefName(dcRef);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        dcName = name;
        return true;
    }

    private static bool TryMapDataCenterToRegion(string? dcName, out string regionName)
    {
        regionName = string.Empty;
        if (string.IsNullOrWhiteSpace(dcName))
        {
            return false;
        }

        var normalized = dcName.Trim().ToLowerInvariant();
        if (normalized is "aether" or "crystal" or "dynamis" or "primal")
        {
            regionName = "North-America";
            return true;
        }

        if (normalized is "chaos" or "light")
        {
            regionName = "Europe";
            return true;
        }

        if (normalized is "elemental" or "gaia" or "mana" or "meteor")
        {
            regionName = "Japan";
            return true;
        }

        if (normalized is "materia")
        {
            regionName = "Oceania";
            return true;
        }

        return false;
    }

    private static string TryGetRowRefName(object? rowRefObject)
    {
        if (rowRefObject is null)
        {
            return string.Empty;
        }

        var isValidProp = rowRefObject.GetType().GetProperty("IsValid");
        if (isValidProp?.PropertyType == typeof(bool))
        {
            var isValid = (bool)(isValidProp.GetValue(rowRefObject) ?? false);
            if (!isValid)
            {
                return string.Empty;
            }
        }

        var valueObject = rowRefObject.GetType().GetProperty("Value")?.GetValue(rowRefObject);
        if (valueObject is null)
        {
            return string.Empty;
        }

        var name = valueObject.GetType().GetProperty("Name")?.GetValue(valueObject)?.ToString();
        return name ?? string.Empty;
    }

    private static string NormalizeUniversalisRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return string.Empty;
        }

        var value = region.Trim().ToLowerInvariant();
        return value switch
        {
            "northamerica" or "north-america" or "north america" => "North-America",
            "na" => "North-America",
            "europe" => "Europe",
            "eu" => "Europe",
            "japan" => "Japan",
            "jp" => "Japan",
            "oceania" => "Oceania",
            "oce" => "Oceania",
            "china" => "China",
            _ => region,
        };
    }

    private static bool IsSupportedRegion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeUniversalisRegion(value);
        return normalized is "North-America" or "Europe" or "Japan" or "Oceania" or "China";
    }

    private static bool IsSupportedDataCenter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is
            "aether" or "crystal" or "dynamis" or "primal" or
            "chaos" or "light" or
            "elemental" or "gaia" or "mana" or "meteor" or
            "materia";
    }

    private string GetUpdatedAgoText()
    {
        if (lastRefreshUtc == DateTime.MinValue)
        {
            return statusText;
        }

        var seconds = Math.Max(0, (int)(DateTime.Now - lastRefreshUtc).TotalSeconds);
        return $"Updated {lastUpdatedItemCount} items {seconds} seconds ago.";
    }

    private string GetStatusText()
    {
        if (lastRefreshUtc > DateTime.MinValue && statusText == "Updated.")
        {
            return GetUpdatedAgoText();
        }

        return statusText;
    }
}
