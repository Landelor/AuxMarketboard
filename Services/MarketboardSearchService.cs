using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AuxMarketboard.Services;

/// <summary>
/// Drives the game's own "ItemSearch" marketboard addon: checks whether it is currently
/// open and, if so, types an item's name into its search box and runs the search.
/// </summary>
public sealed class MarketboardSearchService
{
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    public MarketboardSearchService(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>
    /// Returns true if the in-game marketboard item search window is currently open and ready.
    /// </summary>
    public unsafe bool IsMarketboardSearchOpen()
    {
        try
        {
            var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            return IsAddonReady(addon);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Failed to check marketboard search addon state.");
            return false;
        }
    }

    /// <summary>
    /// If the marketboard search window is open, types the given item name into its search box
    /// and triggers a search. Returns true if the search was actually triggered.
    /// </summary>
    public unsafe bool SearchForItem(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        try
        {
            var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            if (!IsAddonReady(addon))
            {
                return false;
            }

            addon->SearchText.SetString(itemName);
            if (addon->SearchTextInput != null)
            {
                addon->SearchTextInput->SetText(itemName);
            }

            addon->RunSearch(false);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to run marketboard search for '{ItemName}'.", itemName);
            return false;
        }
    }

    private static unsafe bool IsAddonReady(AddonItemSearch* addon)
    {
        return addon != null && addon->IsVisible && addon->IsReady;
    }
}
