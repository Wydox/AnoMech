using System.Diagnostics;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Map;
using AnoMech.Core.Native;
using AnoMech.Windows;
using AnoMech.Pointers;

namespace AnoMech;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private const string CommandName = "/anomech";
    private const string CommandAlias = "/ano";

    public Configuration Configuration { get; init; }
    internal static Configuration Config { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("AnoMech");
    public Game Game { get; }
    // SimObjects reach engine singletons through these statics (mirrors the
    // Plugin.* PluginService pattern).
    internal static Game GameInstance { get; private set; } = null!;
    // Session-lifetime input hooks, owned here (not Game) so they're hooked once
    // per load rather than per scenario. SimPlayer is the sole writer of their
    // flags — it reconciles them from its own state each tick.
    internal static LocalPlayerInputHooks PlayerInputHooks { get; private set; } = null!;
    internal static LogManager LogManager { get; private set; } = null!;
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
#if DEBUG
    private DamageDebugWindow DamageDebugWindow { get; init; }
#endif

    // ENGINE FIX: full-precision per-frame delta so the scenario clock tracks true wall time.
    // The scenario timeline integrates the per-frame delta over minutes, so that delta MUST sum
    // to true wall time. Dalamud's IFramework.UpdateDelta is
    //     TimeSpan.FromMilliseconds(stopwatch.ElapsedMilliseconds)  // whole ms, truncated
    // and the stopwatch is Restart()ed each frame, so the sub-millisecond remainder is dropped
    // with no carry. Integrated frame-after-frame, that floored fraction compounds into seconds
    // of drift over a long phase (≈14–24 s by the end of UMAD P3Eq at 144 fps; loss/frame =
    // frac(1000/fps), so it's framerate-dependent and ≈0 at a clean 100/200 fps). The scheduler
    // clock runs slow, so scheduled events (cast-bar starts) fire progressively late vs the VoD,
    // while the game's own cast-bar fill / animations stay on true time. Measure the delta here
    // from a high-res monotonic clock that carries the remainder instead of trusting UpdateDelta.
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private double lastFrameElapsed;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config = Configuration;

        LogManager = new LogManager();
        if (Config.EnableEventLogging) LogManager.Open();

        PlayerInputHooks = new LocalPlayerInputHooks(GameInterop);
        Game = new Game();
        GameInstance = Game;
        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
#if DEBUG
        DamageDebugWindow = new DamageDebugWindow(this);
        WindowSystem.AddWindow(DamageDebugWindow);
#endif

        if (Config.OpenSimMenuOnInn && ZoneSession.IsInInn())
            MainWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AnoMech. Subcommands: config, start, reset, leave"
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /anomech"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        Framework.Update += OnFrameworkUpdate;

        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        DutyState.DutyStarted += OnDutyStarted;
        DutyState.DutyWiped += OnDutyWiped;
        DutyState.DutyCompleted += OnDutyCompleted;

        // Initialize Pointers
        CharacterManagerPointers.Initialize();
        EventFrameworkPointers.Initialize();
        EventObjectManagerPointers.Initialize();
        EventObjectPointers.Initialize();
        GameMainPointers.Initialize();
        ModelContainerPointers.Initialize();
        PacketDispatcherPointers.Initialize();
        StatusManagerPointers.Initialize();
        TimelineContainerPointers.Initialize();
        VfxContainerPointers.Initialize();
        VfxDataPointers.Initialize();

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        DutyState.DutyStarted -= OnDutyStarted;
        DutyState.DutyWiped -= OnDutyWiped;
        DutyState.DutyCompleted -= OnDutyCompleted;

        WindowSystem.RemoveAllWindows();

        Game.Dispose();
        // After Game.Dispose so World.Dispose → SimPlayer.Despawn can still clear
        // the lock flags through the hooks before they're torn down.
        PlayerInputHooks.Dispose();
        LogManager.Dispose();
        ConfigWindow.Dispose();
        MainWindow.Dispose();
#if DEBUG
        DamageDebugWindow.Dispose();
#endif

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Self-measured delta (see frameClock above): full-precision, remainder carried, so the
        // integrated scenario clock tracks real wall time instead of Dalamud's whole-ms UpdateDelta.
        var now = frameClock.Elapsed.TotalSeconds;
        var delta = now - lastFrameElapsed;
        lastFrameElapsed = now;
        Game.Tick((float)delta);
    }

    private void OnTerritoryChanged(uint territory)
    {
        var row = DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territory);
        var isInn = row?.TerritoryIntendedUse.RowId == 2; // TerritoryIntendedUse.Inn
        if (!isInn)
        {
            var name = row?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
            LogManager.LogEnterInstance(territory, name);
        }

        if (!isInn)
        {
            MainWindow.IsOpen = false;
            return;
        }
        if (Config.OpenSimMenuOnInn)
            MainWindow.IsOpen = true;
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
        => LogManager.LogCombatStart(args.TerritoryType.RowId);

    private void OnDutyWiped(IDutyStateEventArgs args)
        => LogManager.LogCombatEnd(args.TerritoryType.RowId, wipe: true);

    private void OnDutyCompleted(IDutyStateEventArgs args)
        => LogManager.LogCombatEnd(args.TerritoryType.RowId, wipe: false);

    private void OnCommand(string command, string args)
    {
        switch (args.Trim())
        {
            case "config":
                ConfigWindow.Toggle();
                break;
            case "start":
                StartSelectedScenario(solo: false);
                break;
            case "start solo":
                StartSelectedScenario(solo: true);
                break;
            case "reset":
                Game.Reset();
                break;
            case "leave":
                Game.Leave();
                break;
            default:
                MainWindow.Toggle();
                break;
        }
    }

    private void StartSelectedScenario(bool solo)
    {
        if (!ZoneSession.IsInInn())
        {
            Log.Warning("Scenarios can only be started from an inn.");
            return;
        }
        if (ZoneSession.IsPlayerBusy())
        {
            Log.Warning("Cannot start a scenario while you are busy (cutscene, NPC event, crafting, etc.).");
            return;
        }
        if (MainWindow.SelectedScenario is not { } scenario)
            return;
        if (solo && !scenario.SupportsSolo)
        {
            Log.Warning($"{scenario.Name} does not support Solo mode.");
            return;
        }
        Game.RunScenario(scenario, MainWindow.SelectedRoleOverride, solo ? null : MainWindow.SelectedStrat);
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
