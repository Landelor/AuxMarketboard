using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AuxMarketboard.Services;
using AuxMarketboard.UI;

namespace AuxMarketboard;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mmb";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("AuxMarketboard");
    private readonly MainWindow mainWindow;
    private readonly UniversalisClient universalisClient;
    private readonly ItemResolver itemResolver;
    private readonly MarketboardCacheService marketboardCacheService;

    public Configuration Configuration { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);
        if (Configuration.Version < 2)
        {
            Configuration.AutoRefreshEnabled = true;
            Configuration.Version = 2;
            Configuration.Save();
        }

        if (Configuration.AutoDetectWorldOnStartup)
        {
            var detected = PlayerState.IsLoaded && PlayerState.HomeWorld.IsValid
                ? PlayerState.HomeWorld.Value.Name.ToString()
                : null;
            if (!string.IsNullOrWhiteSpace(detected))
            {
                Configuration.WorldOrDataCenter = detected;
                Configuration.Save();
            }
        }
        else if (string.IsNullOrWhiteSpace(Configuration.WorldOrDataCenter))
        {
            Configuration.WorldOrDataCenter = "Oceania";
            Configuration.Save();
        }

        universalisClient = new UniversalisClient();
        itemResolver = new ItemResolver(DataManager);
        marketboardCacheService = new MarketboardCacheService(MarketBoard, Log);
        var marketboardSearchService = new MarketboardSearchService(GameGui, Log);
        mainWindow = new MainWindow(Configuration, universalisClient, itemResolver, marketboardSearchService, marketboardCacheService);

        windowSystem.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AuxMarketboard",
        });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        universalisClient.Dispose();
        marketboardCacheService.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainUi();
    }

    private void ToggleMainUi()
    {
        mainWindow.Toggle();
    }
}
