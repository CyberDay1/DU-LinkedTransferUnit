using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend;
using Backend.Scenegraph;
using Backend.Storage;
using LinkedTransferUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NQ;
using NQ.Grains.Core;
using NQ.Interfaces;
using NQ.RDMS;
using NQutils;
using NQutils.Sql;
using Orleans;

/// Entry point. The Orleans host looks for a class named MyDuMod implementing IMod
/// in every Mods/Mod*.dll.
public class MyDuMod : IMod
{
    public const string ModName = "LinkedTransferUnit";
    // Right-click menu actions (ModActionContext.Element).
    private const ulong ActionStatus = 1;
    private const ulong ActionConfigure = 2;
    private const ulong ActionPairSource = 3;      // on a unit
    private const ulong ActionPairDestination = 4; // on a unit
    private const ulong ActionPairWith = 5;        // on the container, hub or tank to pair
    private const ulong ActionReloadArt = 6;       // anywhere: send the unit art to me again
    // Actions sent by the config panel with CPPMod.sendModAction.
    private const ulong ActionSave = 201;
    private const ulong ActionStart = 202;
    private const ulong ActionStop = 203;
    private const ulong ActionClientLog = 204;
    private const ulong ActionUnpair = 205;
    private const ulong ActionBeginPair = 206;
    private const ulong ActionStockOpened = 207;   // the stock industry panel opened; which unit is it?
    private const ulong ActionHello = 208;         // the script loaded in the client
    private const ulong ActionCatalog = 209;       // item picker: browse a group or search
    private const ulong ActionSaveFilter = 210;
    // Admin-only, not reachable from the stock UI: force-forgets a unit's saved config right
    // away, bypassing UnitConfigKeepHours. Lets a test harness leave no units.json residue.
    private const ulong ActionPurgeConfig = 211;
    // Admin-only, DEBUG: arms an injected fault on a unit (needs FaultInjection: true in the
    // config file, re-read on every call). Payload {"kind": "throw-after-debit"|..., "count": n, "holdMs": n}.
    private const ulong ActionArmFault = 212;
    // Admin-only: resolves every in-flight journal entry now (what startup does after a crash).
    private const ulong ActionResolveJournal = 213;
    private const ulong ActionSetOutput = 214;     // {"mode": "inorder" | "roundrobin"}
    private const ulong ActionMoveDestination = 215; // {"elementId": "...", "dir": -1 | 1}

    private IServiceProvider isp;
    private IClusterClient orleans;
    private ILogger logger;
    private IGameplayBank bank;
    private LtuConfig cfg = new();
    private TransferEngine engine;
    private UnitStore store;
    private MoveJournal journal;
    private IPub pub;
    private ISql sql;
    private string panelScript = "";
    private string scriptVersion = "";
    private Dictionary<string, string> iconIndex = new();
    private Catalog catalog;
    private readonly object catalogGate = new();
    private readonly ConcurrentDictionary<ulong, InjectState> injected = new();

    private sealed class InjectState
    {
        public readonly object Lock = new();
        public CancellationTokenSource Cts;
        public bool Confirmed;
    }
    private readonly ConcurrentDictionary<ulong, (DateTime start, int count)> clientLogBudget = new();
    private readonly CancellationTokenSource cts = new();
    private readonly PairingSessions pairing = new();
    private AssetPush assets;

    public string GetName() => ModName;

    public async Task Initialize(IServiceProvider isp)
    {
        this.isp = isp;
        orleans = isp.GetRequiredService<IClusterClient>();
        logger = isp.GetRequiredService<ILogger<MyDuMod>>();
        bank = isp.GetRequiredService<IGameplayBank>();
        pub = isp.GetRequiredService<IPub>();
        sql = isp.GetRequiredService<ISql>();

        string source;
        try
        {
            (cfg, source) = LtuConfig.Load(LtuConfig.DefaultPath);
        }
        catch (Exception e)
        {
            logger.LogError(e, "LinkedTransferUnit: config unreadable, using defaults");
            cfg = new LtuConfig();
            source = "defaults (config unreadable)";
        }
        logger.LogInformation("LinkedTransferUnit: initializing, config from {source}", source);

        await EnsureItem();
        assets = new AssetPush(pub, logger);
        logger.LogInformation("LinkedTransferUnit: client art {state}", cfg.ClientAssets
            ? $"ON (variant {cfg.AssetVariant}, folder {cfg.ClientAssetDir})" : "off (stock transfer unit look)");

        store = new UnitStore(cfg.StorePath);
        try
        {
            store.Load();
            logger.LogInformation("LinkedTransferUnit: {count} unit config(s) loaded from {path}", store.Count, cfg.StorePath);
        }
        catch (Exception e)
        {
            logger.LogError(e, "LinkedTransferUnit: unit store {path} unreadable; starting empty", cfg.StorePath);
        }

        // Same compaction as the Tool Wheel: the script travels to every client at login.
        using (var s = typeof(MyDuMod).Assembly.GetManifestResourceStream("LinkedTransferUnit.ltu_panel.js"))
        using (var r = new StreamReader(s))
            panelScript = string.Join("\n", r.ReadToEnd().Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("//")));
        using (var sha = SHA1.Create())
            scriptVersion = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(panelScript)))[..10].ToLowerInvariant();
        try
        {
            using var s = typeof(MyDuMod).Assembly.GetManifestResourceStream("LinkedTransferUnit.IconIndex.json");
            iconIndex = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "LinkedTransferUnit: icon index unreadable; the item picker shows no icons");
        }
        bank.RegisterOnChange(() => { lock (catalogGate) catalog = null; });
        _ = Task.Run(() =>
        {
            try { GetCatalog(); }
            catch (Exception e) { logger.LogWarning(e, "LinkedTransferUnit: item catalogue could not be built; the filter picker will retry"); }
        });

        journal = new MoveJournal(MoveJournal.PathNextTo(cfg.StorePath));
        try
        {
            journal.Load();
            if (journal.Count > 0)
                logger.LogWarning("LinkedTransferUnit: {count} interrupted move(s) in {path}; checking them before the first cycle", journal.Count, journal.Path);
        }
        catch (Exception e)
        {
            logger.LogError(e, "LinkedTransferUnit: in-flight journal {path} unreadable; moving it aside", journal.Path);
            try { File.Move(journal.Path, journal.Path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch { }
        }
        if (cfg.FaultInjection)
            logger.LogWarning("LinkedTransferUnit: FaultInjection is ON in the config: admins can arm injected faults (debug only)");

        engine = new TransferEngine(isp, orleans, bank,
            isp.GetRequiredService<IScenegraph>(),
            isp.GetRequiredService<IItemStorageService>(),
            sql, logger, cfg, store, journal);

        if (cfg.HookStockStartStop)
        {
            try
            {
                new IndustryHooks(orleans, engine, logger).Register(isp.GetRequiredService<IHookCallManager>(), cfg.PatchIndustryStatus);
                logger.LogInformation("LinkedTransferUnit: stock industry Start/Stop hooked{status}",
                    cfg.PatchIndustryStatus ? ", Status patched" : "");
            }
            catch (Exception e)
            {
                logger.LogError(e, "LinkedTransferUnit: could not hook the stock industry calls; use Configure > Start/Stop");
            }
        }
        logger.LogInformation("LinkedTransferUnit: running state {req}, name routes {names}",
            cfg.RequireRunning ? "required (units move items only while RUNNING)" : "not required",
            cfg.AllowNameRoutes ? "allowed when no prefixes are saved" : "off");
        _ = Task.Run(() => engine.RunForever(cts.Token));
    }

    /// Add the item to the item bank when it is missing. It is a child of the stock
    /// transfer unit and aliases its art, so clients render the stock model.
    private async Task EnsureItem()
    {
        if (bank.GetDefinition(cfg.ItemName) != null)
        {
            logger.LogInformation("LinkedTransferUnit: item {item} present in item bank (id {id})",
                cfg.ItemName, bank.GetDefinition(cfg.ItemName).Id);
            await SyncAssetAlias();
            return;
        }
        if (!cfg.CreateItemIfMissing)
        {
            logger.LogWarning("LinkedTransferUnit: item {item} missing and CreateItemIfMissing is off; add it in the backoffice", cfg.ItemName);
            return;
        }
        var yaml = BuildItemYaml();
        try
        {
            await bank.Import(yaml, false);
            var def = bank.GetDefinition(cfg.ItemName);
            logger.LogInformation("LinkedTransferUnit: created item {item} under {parent} (id {id})",
                cfg.ItemName, cfg.ItemParent, def?.Id);
        }
        catch (Exception e)
        {
            logger.LogError(e, "LinkedTransferUnit: could not create item {item}; add it in the backoffice. YAML was:\n{yaml}", cfg.ItemName, yaml);
        }
    }

    /// Keeps the item's assetAlias in step with ClientAssets: cleared while the mod sends its
    /// own element definition, the stock alias otherwise. Re-imports only this item.
    private async Task SyncAssetAlias()
    {
        try
        {
            var rows = await sql.Query<string>("SELECT yaml FROM item_definition WHERE name = @1", cfg.ItemName);
            var yaml = rows.FirstOrDefault() ?? "";
            var current = "";
            foreach (var line in yaml.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("assetAlias:"))
                    current = t.Substring("assetAlias:".Length).Trim().Trim('"');
            }
            var want = cfg.EffectiveAssetAlias;
            if (current == want)
            {
                logger.LogInformation("LinkedTransferUnit: item assetAlias is \"{alias}\" (as configured)", current);
                return;
            }
            await bank.Import(BuildItemYaml(), false);
            logger.LogInformation("LinkedTransferUnit: item assetAlias changed from \"{from}\" to \"{to}\"", current, want);
        }
        catch (Exception e)
        {
            logger.LogError(e, "LinkedTransferUnit: could not sync the item's assetAlias; set it in the backoffice");
        }
    }

    private string BuildItemYaml()
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var lines = new List<string>
        {
            $"{cfg.ItemName}:",
            $"  parent: {cfg.ItemParent}",
            $"  displayName: {Q(cfg.ItemDisplayName)}",
            $"  subdescription: {Q(cfg.ItemDescription)}",
        };
        lines.Add($"  assetAlias: {Q(cfg.EffectiveAssetAlias)}");
        return string.Join("\n", lines) + "\n";
    }

    public Task<ModInfo> GetModInfoFor(ulong playerId, bool isPlayerAdmin)
    {
        // A client asks for mod info when it connects. Its HUD drops "modinjectjs" until it is
        // ready, so the script is re-sent on a schedule until it reports back (ActionHello).
        // On the first connection after a restart this runs before Initialize has finished;
        // the injection loop waits for the script.
        if (cfg.InjectStockPanel)
            StartInjection(playerId);
        _ = Task.Run(() => PushClientArt(playerId));
        return Task.FromResult(new ModInfo
        {
            name = ModName,
            actions = new List<ModActionDefinition>
            {
                new ModActionDefinition
                {
                    id = ActionConfigure,
                    label = "Linked Transfer Unit\\Configure",
                    context = ModActionContext.Element,
                },
                new ModActionDefinition
                {
                    id = ActionStatus,
                    label = "Linked Transfer Unit\\Status",
                    context = ModActionContext.Element,
                },
                new ModActionDefinition
                {
                    id = ActionPairSource,
                    label = "Linked Transfer Unit\\Pair source",
                    context = ModActionContext.Element,
                },
                new ModActionDefinition
                {
                    id = ActionPairDestination,
                    label = "Linked Transfer Unit\\Pair destination",
                    context = ModActionContext.Element,
                },
                new ModActionDefinition
                {
                    id = ActionPairWith,
                    label = "Linked Transfer Unit\\Pair with transfer unit",
                    context = ModActionContext.Element,
                },
                new ModActionDefinition
                {
                    id = ActionReloadArt,
                    label = "Linked Transfer Unit\\Reload unit art",
                    context = ModActionContext.Global,
                },
            },
        });
    }

    public async Task TriggerAction(ulong playerId, ModAction action)
    {
        if (engine == null)
            return;
        try
        {
            switch (action.actionId)
            {
                case ActionStatus: await ShowStatus(playerId, action.elementId); break;
                case ActionConfigure: await ShowPanel(playerId, action.elementId); break;
                case ActionSave: await Save(playerId, action.elementId, action.payload); break;
                case ActionStart: await StartStop(playerId, action.elementId, true); break;
                case ActionStop: await StartStop(playerId, action.elementId, false); break;
                case ActionClientLog: ClientLog(playerId, action.payload); break;
                case ActionPairSource: await BeginPairing(playerId, action.elementId, PairRole.Source, fromPanel: false); break;
                case ActionPairDestination: await BeginPairing(playerId, action.elementId, PairRole.Destination, fromPanel: false); break;
                case ActionPairWith: await PairWith(playerId, action.elementId); break;
                case ActionReloadArt: await ReloadArt(playerId); break;
                case ActionUnpair: await Unpair(playerId, action.elementId, action.payload); break;
                case ActionStockOpened: await StockOpened(playerId, action.payload); break;
                case ActionHello: Hello(playerId, action.payload); break;
                case ActionCatalog: await CatalogQuery(playerId, action.payload); break;
                case ActionSaveFilter: await SaveFilter(playerId, action.elementId, action.payload); break;
                case ActionPurgeConfig: await PurgeConfig(playerId, action.elementId); break;
                case ActionArmFault: await ArmFault(playerId, action.elementId, action.payload); break;
                case ActionResolveJournal: await ResolveJournalNow(playerId); break;
                case ActionSetOutput: await SetOutput(playerId, action.elementId, action.payload); break;
                case ActionMoveDestination: await MoveDestination(playerId, action.elementId, action.payload); break;
                case ActionBeginPair:
                    if (PairRules.TryParseRolePayload(action.payload, false, out var role, out _, out var perr))
                        await BeginPairing(playerId, action.elementId, role, fromPanel: true);
                    else
                        await Reply(playerId, action.elementId, null, perr, error: true, saved: false);
                    break;
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "LinkedTransferUnit: action {action} from player {player} failed", action.actionId, playerId);
            if (action.actionId < 100)
                await Notify(playerId, "Linked Transfer Unit: unavailable (" + e.Message + ")");
            else if (action.actionId != ActionClientLog && action.actionId != ActionHello && action.actionId != ActionStockOpened)
                await Reply(playerId, action.elementId, null, "Failed: " + e.Message, error: true, saved: false);
        }
    }

    /// Sends the unit's art to a connecting client. The config is re-read so AssetVariant and
    /// ClientAssets take effect at the next login without a restart (the item's assetAlias
    /// follows ClientAssets only at boot).
    private async Task PushClientArt(ulong playerId)
    {
        try
        {
            for (int i = 0; i < 120 && assets == null; i++)
                await Task.Delay(500);
            if (assets == null)
                return;
            LtuConfig live;
            try { live = LtuConfig.Load(LtuConfig.DefaultPath).cfg; }
            catch { live = cfg; }
            await assets.SendTo(playerId, live);
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "LinkedTransferUnit: sending client art to player {player} failed", playerId);
        }
    }

    private async Task ReloadArt(ulong playerId)
    {
        var live = LtuConfig.Load(LtuConfig.DefaultPath).cfg;
        if (!live.ClientAssets)
        {
            await Notify(playerId, "Linked Transfer Unit: client art is off on this server (stock look)");
            return;
        }
        await assets.SendTo(playerId, live, force: true);
        await Notify(playerId, $"Linked Transfer Unit: unit art sent (variant {live.AssetVariant})");
    }

    private async Task ShowStatus(ulong playerId, ulong elementId)
    {
        string msg;
        var u = await engine.FindUnit(elementId);
        if (u == null)
            msg = "This is not a Linked Transfer Unit.";
        else
        {
            var r = await engine.ProcessUnit(u, dryRun: true);
            msg = "Linked Transfer Unit: " + r.Status + $" | output: {OutputRules.Label(r.Output).ToLowerInvariant()}";
            if (engine.Reports.TryGetValue(elementId, out var last) && last.MovedLitres > 0)
                msg += $" | last move {last.MovedLitres:F1} L at {last.At:HH:mm:ss} UTC";
            if (engine.LastError(elementId) is { } err)
                msg += $" | last error at {err.at:HH:mm:ss} UTC: {err.msg}";
        }
        await Notify(playerId, msg);
    }

    /// Admin-only: force-forgets a unit's saved config right away (store.Remove), bypassing the
    /// UnitConfigKeepHours grace that DropMissingUnit / Prune otherwise respect. Not reachable
    /// from the stock UI or the config panel; works purely on the store, so it still works once
    /// the unit's element is already gone. Intended for test/admin cleanup.
    private async Task PurgeConfig(ulong playerId, ulong elementId)
    {
        if (!await IsAdmin(playerId))
        {
            logger.LogWarning("LinkedTransferUnit: player {player} tried to purge unit {unit}'s config but is not an admin", playerId, elementId);
            return;
        }
        var removed = store.Remove(elementId);
        logger.LogInformation("LinkedTransferUnit: unit {unit} config {what} by admin {player}",
            elementId, removed ? "purged" : "purge requested but no entry existed", playerId);
    }

    /// DEBUG, admin-only, and only while the config file says FaultInjection: true (read now,
    /// not at boot, so the flag can be flipped for one harness run without a restart).
    private async Task ArmFault(ulong playerId, ulong elementId, string payload)
    {
        bool enabled;
        try { enabled = LtuConfig.Load(LtuConfig.DefaultPath).cfg.FaultInjection; }
        catch { enabled = false; }
        if (!enabled || !await IsAdmin(playerId))
        {
            logger.LogWarning("LinkedTransferUnit: player {player} tried to arm a fault on unit {unit}; refused ({why})",
                playerId, elementId, enabled ? "not an admin" : "FaultInjection is off");
            return;
        }
        string kindText = null;
        int count = 1, holdMs = 0;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(payload) ? "{}" : payload);
            var r = doc.RootElement;
            if (r.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String) kindText = k.GetString();
            if (r.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number) count = c.GetInt32();
            if (r.TryGetProperty("holdMs", out var h) && h.ValueKind == JsonValueKind.Number) holdMs = h.GetInt32();
        }
        catch (JsonException) { }
        if (kindText == "off")
        {
            engine.Faults.Disarm(elementId);
            logger.LogWarning("LinkedTransferUnit: [fault injection] unit {unit} disarmed by admin {player}", elementId, playerId);
            return;
        }
        if (!FaultPlan.TryParseKind(kindText, out var kind))
        {
            logger.LogWarning("LinkedTransferUnit: [fault injection] unknown fault kind \"{kind}\"", kindText);
            return;
        }
        engine.Faults.Arm(elementId, kind, count, holdMs);
        logger.LogWarning("LinkedTransferUnit: [fault injection] unit {unit} armed with {kind} x{count} (hold {ms} ms) by admin {player}",
            elementId, kind, count, holdMs, playerId);
    }

    private async Task ResolveJournalNow(ulong playerId)
    {
        if (!await IsAdmin(playerId)) return;
        var done = await engine.ResolveJournal(all: true);
        logger.LogInformation("LinkedTransferUnit: admin {player} resolved the in-flight journal: {n} entr(ies) {what}", playerId, done.Count,
            string.Join(", ", done.Select(d => d.move.Id + "=" + MoveRules.Name(d.outcome))));
    }

    private async Task<bool> IsAdmin(ulong playerId) =>
        (await sql.Query<bool>("SELECT admin FROM player WHERE id = @1", playerId)).FirstOrDefault();

    // ------------------------------------------------------------------ config panel

    /// (Element Edit, Element Use or Edit) for the player on the unit; admins pass.
    private async Task<(bool edit, bool use)> RightsOn(ulong playerId, UnitRef u)
    {
        var asset = new AssetId { type = AssetType.Element, construct = u.ConstructId, element = u.ElementId };
        var r = await orleans.GetRDMSRightGrain(playerId).GetRightsForPlayerOnAsset(playerId, asset, true);
        var edit = r.rights.Contains(Right.ElementEdit);
        return (edit, edit || r.rights.Contains(Right.ElementUse));
    }

    /// Everything the panel shows for one unit.
    private async Task<Dictionary<string, object>> PanelData(ulong playerId, UnitRef u, string msg, bool error, bool saved)
    {
        var (edit, use) = await RightsOn(playerId, u);
        var unit = await orleans.GetConstructElementsGrain(u.ConstructId).GetElement(u.ElementId);
        var report = await engine.ProcessUnit(u, dryRun: true);
        string unitName = unit.properties != null && unit.properties.TryGetValue("name", out var pv) && pv != null ? pv.stringValue ?? "" : "";
        var settings = engine.Settings(u.ElementId);
        var (route, origin) = UnitSettingsRules.Resolve(settings, unitName, cfg.AllowNameRoutes, cfg.RouteSeparator);
        var session = pairing.Get(playerId, DateTime.UtcNow);
        object pairingNow = session != null && session.UnitId == u.ElementId
            ? new Dictionary<string, object>
            {
                ["role"] = PairRules.RoleName(session.Role),
                ["secondsLeft"] = (int)Math.Ceiling((session.Expires - DateTime.UtcNow).TotalSeconds),
            }
            : null;
        return new Dictionary<string, object>
        {
            ["version"] = 2,
            ["mod"] = ModName,
            ["elementId"] = u.ElementId,
            ["constructId"] = u.ConstructId,
            ["unitName"] = unitName,
            // Saved prefixes only (a name route shows in "route" with origin "name").
            ["source"] = settings?.Source ?? "",
            ["destination"] = settings?.Destination ?? "",
            ["origin"] = origin == RouteOrigin.Panel ? "panel" : origin == RouteOrigin.Name ? "name" : "none",
            ["route"] = route?.ToString() ?? "",
            ["running"] = report.Running,
            ["requireRunning"] = cfg.RequireRunning,
            ["status"] = report.Status,
            ["canEdit"] = edit,
            ["canUse"] = use,
            ["maxLen"] = cfg.MaxPrefixLength,
            ["maxPairs"] = cfg.MaxPairsPerRole,
            ["rangeMeters"] = cfg.MaxRangeMeters,
            ["pairing"] = pairingNow,
            ["sources"] = report.Pairs.Where(p => p.Role == PairRole.Source).Select(PairJson).ToList(),
            ["destinations"] = report.Pairs.Where(p => p.Role == PairRole.Destination).Select(PairJson).ToList(),
            ["probe"] = cfg.ProbeStockPanel,
            ["filter"] = FilterJson(settings?.Filter),
            ["outputMode"] = OutputRules.Name(settings?.OutputModeValue ?? OutputMode.InOrder),
            // Destinations found on this check, in the order the mode uses them.
            ["outputOrder"] = report.DestinationOrder,
            // STATUS tab detail; the main view shows none of it.
            ["actor"] = report.Actor,
            ["sourcesFound"] = report.Sources,
            ["destinationsFound"] = report.Destinations,
            ["deniedByRights"] = report.DeniedByRights,
            ["lastMove"] = engine.Reports.TryGetValue(u.ElementId, out var last) && last.MovedLitres > 0
                ? new Dictionary<string, object> { ["litres"] = Math.Round(last.MovedLitres, 1), ["at"] = last.At.ToString("HH:mm:ss") + " UTC" }
                : null,
            ["lastError"] = engine.LastError(u.ElementId) is { } le
                ? new Dictionary<string, object> { ["msg"] = le.msg, ["at"] = le.at.ToString("HH:mm:ss") + " UTC" }
                : null,
            ["msg"] = msg ?? "",
            ["error"] = error,
            ["saved"] = saved,
        };
    }

    /// One pair as the panel receives it. Ids go as strings too: the HUD's numbers are doubles.
    private static Dictionary<string, object> PairJson(PairView p) => new()
    {
        ["role"] = PairRules.RoleName(p.Role),
        ["elementId"] = p.ElementId,
        ["elementKey"] = p.ElementId.ToString(),
        ["constructId"] = p.ConstructId,
        ["constructName"] = p.ConstructName,
        ["localId"] = p.LocalId,
        ["name"] = p.Name,
        ["typeName"] = p.TypeName,
        ["status"] = p.StatusName,
        ["note"] = p.Note,
    };

    private async Task ShowPanel(ulong playerId, ulong elementId)
    {
        var u = await engine.FindUnit(elementId);
        if (u == null)
        {
            await Notify(playerId, "This is not a Linked Transfer Unit.");
            return;
        }
        var data = await PanelData(playerId, u, "", false, false);
        await SendHudEvent(playerId, "modinjectjs", Script(data));
        logger.LogInformation("LinkedTransferUnit: config panel sent to player {player} for unit {unit}", playerId, elementId);
    }

    private async Task Save(ulong playerId, ulong elementId, string payload)
    {
        var u = await engine.FindUnit(elementId);
        if (u == null) return;
        var (edit, _) = await RightsOn(playerId, u);
        if (!edit)
        {
            await Reply(playerId, u.ElementId, u, "You need the Element Edit right on this unit to change it.", error: true, saved: false);
            return;
        }
        if (!UnitSettingsRules.TryParsePayload(payload, out var s0, out var d0, out var err) ||
            !UnitSettingsRules.Validate(s0, d0, cfg.MaxPrefixLength, cfg.CaseInsensitive, out var src, out var dst, out err))
        {
            await Reply(playerId, u.ElementId, u, err, error: true, saved: false);
            return;
        }
        var unit = await orleans.GetConstructElementsGrain(u.ConstructId).GetElement(u.ElementId);
        store.Update(u.ElementId, s =>
        {
            s.Source = src;
            s.Destination = dst;
            s.UpdatedBy = playerId;
            s.UpdatedAt = DateTime.UtcNow;
            TransferEngine.Remember(s, u, unit);
            return true;
        });
        logger.LogInformation("LinkedTransferUnit: unit {unit} prefixes set to \"{src}\" / \"{dst}\" by player {player}", u.ElementId, src, dst, playerId);
        await Reply(playerId, u.ElementId, u, "Saved.", error: false, saved: true);
    }

    private async Task StartStop(ulong playerId, ulong elementId, bool start)
    {
        var u = await engine.FindUnit(elementId);
        if (u == null) return;
        var (_, use) = await RightsOn(playerId, u);
        if (!use)
        {
            await Reply(playerId, u.ElementId, u, "You need the Element Use right on this unit to start or stop it.", error: true, saved: false);
            return;
        }
        await engine.SetRunning(u, start);
        logger.LogInformation("LinkedTransferUnit: unit {unit} {what} by player {player} (config panel)", u.ElementId, start ? "started" : "stopped", playerId);
        await Reply(playerId, u.ElementId, u, start ? "Started." : "Stopped.", error: false, saved: false);
    }

    // ------------------------------------------------------------------ output mode

    private async Task SetOutput(ulong playerId, ulong elementId, string payload)
    {
        var u = await engine.FindUnit(elementId);
        if (u == null) return;
        var (edit, _) = await RightsOn(playerId, u);
        if (!edit)
        {
            await Reply(playerId, u.ElementId, u, "You need the Element Edit right on this unit to change its output.", error: true, saved: false);
            return;
        }
        if (!OutputRules.TryParseModePayload(payload, out var mode, out var err))
        {
            await Reply(playerId, u.ElementId, u, err, error: true, saved: false);
            return;
        }
        engine.FlushCursor(u.ElementId);
        store.Update(u.ElementId, s =>
        {
            if (s.OutputModeValue == mode && s.OutputMode == OutputRules.Name(mode)) return false;
            s.OutputMode = OutputRules.Name(mode);
            s.UpdatedBy = playerId;
            s.UpdatedAt = DateTime.UtcNow;
            return true;
        });
        logger.LogInformation("LinkedTransferUnit: unit {unit} output set to {mode} by player {player}", u.ElementId, OutputRules.Name(mode), playerId);
        await Reply(playerId, u.ElementId, u, mode == OutputMode.RoundRobin
            ? "Output: round robin. Each cycle is shared across the destinations."
            : "Output: in order. The first destination fills before the next.", error: false, saved: false);
    }

    private async Task MoveDestination(ulong playerId, ulong elementId, string payload)
    {
        var u = await engine.FindUnit(elementId);
        if (u == null) return;
        var (edit, _) = await RightsOn(playerId, u);
        if (!edit)
        {
            await Reply(playerId, u.ElementId, u, "You need the Element Edit right on this unit to reorder it.", error: true, saved: false);
            return;
        }
        if (!OutputRules.TryParseMovePayload(payload, out var target, out var dir, out var err))
        {
            await Reply(playerId, u.ElementId, u, err, error: true, saved: false);
            return;
        }
        bool moved = false;
        store.Update(u.ElementId, s => moved = OutputRules.MovePair(s.DestinationPairs, target, dir));
        if (moved)
            logger.LogInformation("LinkedTransferUnit: unit {unit} destination {element} moved {dir} by player {player}", u.ElementId, target, dir < 0 ? "up" : "down", playerId);
        await Reply(playerId, u.ElementId, u, moved ? "Order saved." : "That destination cannot move further.", error: !moved, saved: false);
    }

    // ------------------------------------------------------------------ pairing

    private string RoleLabel(PairRole r) => PairRules.RoleName(r);

    /// "Pair source" / "Pair destination" on a unit: the player enters pairing mode for it.
    private async Task BeginPairing(ulong playerId, ulong elementId, PairRole role, bool fromPanel)
    {
        var u = await engine.FindUnit(elementId);
        if (u == null)
        {
            await Notify(playerId, "This is not a Linked Transfer Unit.");
            return;
        }
        var (edit, _) = await RightsOn(playerId, u);
        if (!edit)
        {
            await Fail(playerId, u, fromPanel, "You need the Element Edit right on this unit to pair it.");
            return;
        }
        var have = engine.Settings(u.ElementId)?.Pairs(role).Count ?? 0;
        if (have >= cfg.MaxPairsPerRole)
        {
            await Fail(playerId, u, fromPanel, $"This unit already has {cfg.MaxPairsPerRole} paired {RoleLabel(role)}s, the maximum. Unpair one first.");
            return;
        }
        pairing.Begin(playerId, u.ElementId, u.ConstructId, role, DateTime.UtcNow, TimeSpan.FromSeconds(cfg.PairingWindowSeconds));
        logger.LogInformation("LinkedTransferUnit: player {player} pairing a {role} for unit {unit}", playerId, RoleLabel(role), u.ElementId);
        await Notify(playerId, $"Linked Transfer Unit: pairing a {RoleLabel(role)} for {cfg.PairingWindowSeconds:F0} s. " +
            $"Right-click a container, hub or fuel tank within {cfg.MaxRangeMeters:F0} m and choose Linked Transfer Unit > Pair with transfer unit.");
        if (fromPanel)
            await Reply(playerId, u.ElementId, u, $"Pairing a {RoleLabel(role)}: right-click the element and choose Pair with transfer unit.", error: false, saved: false);
    }

    private async Task Fail(ulong playerId, UnitRef u, bool fromPanel, string msg)
    {
        if (fromPanel) await Reply(playerId, u.ElementId, u, msg, error: true, saved: false);
        else await Notify(playerId, "Linked Transfer Unit: " + msg);
    }

    /// "Pair with transfer unit" on the element to pair.
    private async Task PairWith(ulong playerId, ulong targetId)
    {
        var now = DateTime.UtcNow;
        var session = pairing.Get(playerId, now);
        if (session == null)
        {
            await Notify(playerId, "Linked Transfer Unit: no unit is waiting to pair. Right-click a Linked Transfer Unit and choose Pair source or Pair destination first.");
            return;
        }
        var role = session.Role;
        var u = await engine.FindUnit(session.UnitId);
        if (u == null)
        {
            pairing.End(playerId);
            await Notify(playerId, "Linked Transfer Unit: the unit you were pairing is gone.");
            return;
        }
        // Gather the facts PairRules.Check rules on; skip the I/O once one has failed.
        var check = new PairRules.PairCheck
        {
            Role = role, ElementId = targetId, RangeMeters = cfg.MaxRangeMeters, Cap = cfg.MaxPairsPerRole,
            IsUnitItself = targetId == u.ElementId,
            Existing = engine.Settings(u.ElementId)?.Pairs(role) ?? new List<PairedElement>(),
        };
        ulong cid = 0, localId = 0, typeId = 0;
        string constructName = "";
        ElementInfo unit = null, target = null;
        if (!check.IsUnitItself)
        {
            var rows = await sql.Query<long, long, long, string>(
                "SELECT e.construct_id, COALESCE(e.local_id, 0), e.element_type_id, COALESCE(c.name, '') FROM element e JOIN construct c ON c.id = e.construct_id " +
                "WHERE e.id = @1 AND c.deleted_at IS NULL", targetId);
            check.OnLiveConstruct = rows.Count > 0;
            if (check.OnLiveConstruct)
            {
                (cid, localId, typeId, constructName) = ((ulong)rows[0].Item1, (ulong)rows[0].Item2, (ulong)rows[0].Item3, rows[0].Item4 ?? "");
                check.IsContainer = bank.GetDefinition(typeId)?.BaseObject is NQutils.Def.ContainerUnit;
            }
        }
        if (check.IsContainer)
        {
            (check.UnitEdit, _) = await RightsOn(playerId, u);
            var needed = role == PairRole.Source ? Right.ContainerRetrieve : Right.ContainerPut;
            var asset = new AssetId { type = AssetType.Element, construct = cid, element = targetId };
            var r = await orleans.GetRDMSRightGrain(playerId).GetRightsForPlayerOnAsset(playerId, asset, true);
            check.TargetRight = r.rights.Contains(needed);
        }
        if (check.UnitEdit && check.TargetRight)
        {
            unit = await orleans.GetConstructElementsGrain(u.ConstructId).GetElement(u.ElementId);
            target = await orleans.GetConstructElementsGrain(cid).GetElement(targetId);
            check.DistanceMeters = await engine.DistanceBetween(unit, target);
        }
        var why = PairRules.Check(check);
        if (why != null)
        {
            await Notify(playerId, "Linked Transfer Unit: " + why);
            return;
        }
        var d = check.DistanceMeters.Value;
        var name = TransferEngine.NameOf(target) ?? "";
        string refused = null;
        int count = 0;
        store.Update(u.ElementId, s =>
        {
            TransferEngine.Remember(s, u, unit);
            var list = s.Pairs(role);
            refused = PairRules.CanAdd(list, targetId, role, cfg.MaxPairsPerRole);
            if (refused != null) return true;   // still saves the unit's key; harmless
            list.Add(new PairedElement
            {
                ElementId = targetId, ConstructId = cid, LocalId = localId, ElementTypeId = typeId, Name = name,
                PairedBy = playerId, PairedAt = now,
            });
            count = list.Count;
            s.UpdatedBy = playerId;
            s.UpdatedAt = now;
            return true;
        });
        if (refused != null)
        {
            await Notify(playerId, "Linked Transfer Unit: " + refused);
            return;
        }
        pairing.Extend(playerId, now, TimeSpan.FromSeconds(cfg.PairingWindowSeconds));
        var label = name.Length > 0 ? name : engine.TypeName(typeId);
        logger.LogInformation("LinkedTransferUnit: unit {unit} paired {role} {element} ({name}) on construct {cid} L{local} by player {player} ({d:F0} m)",
            u.ElementId, RoleLabel(role), targetId, label, cid, localId, playerId, d);
        await Notify(playerId, $"Linked Transfer Unit: paired {label} on {constructName} as {RoleLabel(role)} {count}/{cfg.MaxPairsPerRole}. " +
            $"Pairing stays on for {cfg.PairingWindowSeconds:F0} s for the next one.");
        await Reply(playerId, u.ElementId, u, $"Paired {label} as {RoleLabel(role)}.", error: false, saved: false);
    }

    /// Unpair from the panel: {"role": "source"|"destination", "elementId": n}.
    private async Task Unpair(ulong playerId, ulong elementId, string payload)
    {
        var u = await engine.FindUnit(elementId);
        if (u == null) return;
        var (edit, _) = await RightsOn(playerId, u);
        if (!edit)
        {
            await Reply(playerId, u.ElementId, u, "You need the Element Edit right on this unit to unpair.", error: true, saved: false);
            return;
        }
        if (!PairRules.TryParseRolePayload(payload, true, out var role, out var target, out var err))
        {
            await Reply(playerId, u.ElementId, u, err, error: true, saved: false);
            return;
        }
        bool removed = false;
        store.Update(u.ElementId, s =>
        {
            removed = s.Pairs(role).RemoveAll(p => p.ElementId == target) > 0;
            return removed;
        });
        if (removed)
            logger.LogInformation("LinkedTransferUnit: unit {unit} unpaired {role} {element} by player {player}", u.ElementId, RoleLabel(role), target, playerId);
        await Reply(playerId, u.ElementId, u, removed ? "Unpaired." : "That element was not paired.", error: !removed, saved: false);
    }

    // ------------------------------------------------------------------ script delivery

    /// The client script with its boot data: `open` is a unit's panel data (Configure), or null
    /// for the login injection that only watches for the stock industry panel.
    private string Script(Dictionary<string, object> open)
    {
        var boot = new Dictionary<string, object>
        {
            ["mod"] = ModName,
            ["version"] = scriptVersion,
            ["probe"] = cfg.ProbeStockPanel,
            ["inject"] = cfg.InjectStockPanel,
            ["open"] = open,
        };
        return panelScript.Replace("__LTU_BOOT__", JsonSerializer.Serialize(boot));
    }

    private void StartInjection(ulong playerId)
    {
        var st = injected.GetOrAdd(playerId, _ => new InjectState());
        CancellationTokenSource my;
        lock (st.Lock)
        {
            st.Cts?.Cancel();
            st.Cts = my = new CancellationTokenSource();
            st.Confirmed = false;
        }
        _ = Task.Run(async () =>
        {
            int attempt = 0;
            double elapsed = 0;
            foreach (var at in cfg.InjectDelaysSeconds.OrderBy(d => d))
            {
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, at - elapsed)), my.Token); }
                catch (TaskCanceledException) { return; }
                elapsed = at;
                if (st.Confirmed || my.IsCancellationRequested) return;
                if (string.IsNullOrEmpty(panelScript) || !cfg.InjectStockPanel) continue;
                attempt++;
                try
                {
                    await SendHudEvent(playerId, "modinjectjs", Script(null));
                    logger.LogInformation("LinkedTransferUnit: injected panel script v{version} into player {player} (login, attempt {n} at +{at:0}s)",
                        scriptVersion, playerId, attempt, at);
                }
                catch (Exception e) { logger.LogWarning(e, "LinkedTransferUnit: injection into player {player} failed", playerId); }
            }
            if (!st.Confirmed && !my.IsCancellationRequested)
                logger.LogWarning("LinkedTransferUnit: player {player} never confirmed the panel script after {n} attempts " +
                    "(game.enableModJS off on that client, or the HUD rejected the script; see the client log)", playerId, attempt);
        });
    }

    private void Hello(ulong playerId, string payload)
    {
        if (injected.TryGetValue(playerId, out var st))
            lock (st.Lock) { st.Confirmed = true; st.Cts?.Cancel(); }
        var v = payload ?? "";
        logger.LogInformation("LinkedTransferUnit: player {player} loaded panel script {version}", playerId, v.Length > 64 ? v.Substring(0, 64) : v);
    }

    // ------------------------------------------------------------------ stock industry panel

    /// The stock industry panel opened on a client. The client sends the ids it saw in the HUD
    /// events just before ({"candidates": ["123", ...]}); the first that is a linked unit wins.
    /// Without one, the industry unit nearest the player is used when it is a linked unit
    /// within StockPanelMatchMeters. The reply is the HUD event "LinkedTransferUnit.stock".
    private async Task StockOpened(ulong playerId, string payload)
    {
        var candidates = new List<ulong>();
        long nonce = 0;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(payload) ? "{}" : payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("nonce", out var nv) && nv.ValueKind == JsonValueKind.Number)
                nv.TryGetInt64(out nonce);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("candidates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var v in arr.EnumerateArray().Take(64))
                {
                    if (v.ValueKind == JsonValueKind.String && ulong.TryParse(v.GetString(), out var id)) candidates.Add(id);
                    else if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out id)) candidates.Add(id);
                }
        }
        catch (JsonException) { }

        UnitRef u = null;
        string how = null;
        foreach (var id in candidates.Distinct())
        {
            if (!engine.IsKnownUnit(id)) continue;
            u = await engine.FindUnit(id);
            if (u != null) { how = "hud event"; break; }
        }
        if (u == null && cfg.StockPanelMatchMeters > 0)
        {
            var near = await engine.NearestIndustryUnit(playerId, cfg.StockPanelMatchMeters);
            if (near != null && engine.IsKnownUnit(near.Value.elementId))
            {
                u = await engine.FindUnit(near.Value.elementId);
                how = $"nearest industry unit, {near.Value.meters:F1} m";
            }
            else if (near != null)
                how = $"nearest industry unit {near.Value.elementId} ({near.Value.meters:F1} m) is not a linked unit";
        }
        if (u == null)
        {
            logger.LogInformation("LinkedTransferUnit: player {player} opened a stock industry panel: not a linked unit ({how}; {n} candidate id(s))",
                playerId, how ?? "no industry unit near", candidates.Count);
            await SendHudEvent(playerId, "LinkedTransferUnit.stock", JsonSerializer.Serialize(new Dictionary<string, object> { ["none"] = true, ["nonce"] = nonce }));
            return;
        }
        logger.LogInformation("LinkedTransferUnit: [unproven] player {player} opened the stock panel of unit {unit} (matched by {how})", playerId, u.ElementId, how);
        var data = await PanelData(playerId, u, "", false, false);
        data["match"] = how;
        data["nonce"] = nonce;
        await SendHudEvent(playerId, "LinkedTransferUnit.stock", JsonSerializer.Serialize(data));
    }

    // ------------------------------------------------------------------ item filter

    private Catalog GetCatalog()
    {
        lock (catalogGate)
        {
            if (catalog != null) return catalog;
            var nodes = bank.GetDefinitions().Select(d => new CatalogNode
            {
                Id = d.Id,
                Name = d.Name ?? "",
                DisplayName = d.BaseObject?.DisplayName ?? "",
                ParentId = d.Parent?.Id ?? 0,
                Hidden = d.BaseObject?.Hidden ?? false,
                AssetAlias = d.BaseObject?.AssetAlias ?? "",
            }).ToList();
            catalog = Catalog.Build(nodes, cfg.CatalogRoot, iconIndex, cfg.CatalogExclude);
            logger.LogInformation("LinkedTransferUnit: item catalogue built: {items} items in {groups} groups under {root} ({defs} definitions in the bank; above the root: {up})",
                catalog.ItemCount, catalog.GroupCount, cfg.CatalogRoot, nodes.Count, catalog.Ancestors.Length > 0 ? catalog.Ancestors : "nothing");
            return catalog;
        }
    }

    private static Dictionary<string, object> EntryJson(Catalog.Entry e) => new()
    {
        ["id"] = e.Id.ToString(),
        ["label"] = e.Label,
        ["icon"] = e.Icon,
        ["group"] = e.IsGroup,
        ["count"] = e.ItemCount,
    };

    /// The unit's filter as the panel shows it: each entry with its label, icon and group path.
    private Dictionary<string, object> FilterJson(ItemFilterSpec f)
    {
        f ??= new ItemFilterSpec();
        var cat = GetCatalog();
        return new Dictionary<string, object>
        {
            ["mode"] = ItemFilter.ModeName(f.ModeValue),
            ["max"] = cfg.MaxFilterEntries,
            ["items"] = (f.Items ?? new()).Select(id =>
            {
                var e = cat.Get(id);
                if (e == null)
                    return new Dictionary<string, object>
                    {
                        ["id"] = id.ToString(), ["label"] = bank.GetDefinition(id)?.Name ?? ("item " + id),
                        ["icon"] = "", ["group"] = false, ["count"] = 0, ["path"] = "", ["missing"] = true,
                    };
                var j = EntryJson(e);
                j["path"] = cat.PathLabel(id);
                return j;
            }).ToList(),
        };
    }

    /// Item picker: {"req": n, "group": "<id>"} browses a group ("" = the root);
    /// {"req": n, "q": "text"} searches. Reply: HUD event "LinkedTransferUnit.catalog".
    private async Task CatalogQuery(ulong playerId, string payload)
    {
        long req = 0;
        string q = null;
        ulong group = 0;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(payload) ? "{}" : payload);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("req", out var rq) && rq.ValueKind == JsonValueKind.Number) rq.TryGetInt64(out req);
                if (r.TryGetProperty("q", out var qq) && qq.ValueKind == JsonValueKind.String) q = qq.GetString();
                if (r.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String) ulong.TryParse(g.GetString(), out group);
            }
        }
        catch (JsonException) { }
        if (q != null && q.Length > 64) q = q.Substring(0, 64);

        var cat = GetCatalog();
        var page = q != null ? cat.Search(q, cfg.CatalogSearchMax) : cat.Browse(group == 0 ? cat.RootId : group, cfg.CatalogBrowseMax);
        Dictionary<string, object> WithPath(Catalog.Entry e)
        {
            var j = EntryJson(e);
            if (q != null) j["path"] = cat.PathLabel(e.Id);
            return j;
        }
        var reply = new Dictionary<string, object>
        {
            ["req"] = req,
            ["kind"] = q != null ? "search" : "browse",
            ["q"] = q ?? "",
            ["group"] = page.Group == null ? null : EntryJson(page.Group),
            ["path"] = page.Path.Select(EntryJson).ToList(),
            ["groups"] = page.Groups.Select(WithPath).ToList(),
            ["items"] = page.Items.Select(WithPath).ToList(),
            ["total"] = page.Total,
            ["truncated"] = page.Truncated,
        };
        await SendHudEvent(playerId, "LinkedTransferUnit.catalog", JsonSerializer.Serialize(reply));
    }

    private async Task SaveFilter(ulong playerId, ulong elementId, string payload)
    {
        var u = await engine.FindUnit(elementId);
        if (u == null) return;
        var (edit, _) = await RightsOn(playerId, u);
        if (!edit)
        {
            await Reply(playerId, u.ElementId, u, "You need the Element Edit right on this unit to change its filter.", error: true, saved: false);
            return;
        }
        var cat = GetCatalog();
        if (!ItemFilter.TryParsePayload(payload, out var mode, out var ids, out var err) ||
            !ItemFilter.Validate(mode, ids, cat.Contains, cfg.MaxFilterEntries, out var spec, out err))
        {
            await Reply(playerId, u.ElementId, u, err, error: true, saved: false);
            return;
        }
        var unit = await orleans.GetConstructElementsGrain(u.ConstructId).GetElement(u.ElementId);
        store.Update(u.ElementId, s =>
        {
            s.Filter = spec;
            s.UpdatedBy = playerId;
            s.UpdatedAt = DateTime.UtcNow;
            TransferEngine.Remember(s, u, unit);
            return true;
        });
        logger.LogInformation("LinkedTransferUnit: unit {unit} filter set to {mode} of {n} entr(ies) by player {player}",
            u.ElementId, spec.Mode, spec.Items.Count, playerId);
        var msg = spec.ModeValue == FilterMode.Off ? "Filter off: every item moves."
            : spec.ModeValue == FilterMode.Whitelist && spec.Items.Count == 0 ? "Filter saved. An empty whitelist moves nothing."
            : "Filter saved.";
        await Reply(playerId, u.ElementId, u, msg, error: false, saved: true);
    }

    /// Sends the open panel its new state (HUD event "LinkedTransferUnit.update").
    private async Task Reply(ulong playerId, ulong elementId, UnitRef? u, string msg, bool error, bool saved)
    {
        Dictionary<string, object> data;
        if (u != null)
            data = await PanelData(playerId, u, msg, error, saved);
        else
            data = new Dictionary<string, object> { ["elementId"] = elementId, ["msg"] = msg, ["error"] = error, ["saved"] = false };
        await SendHudEvent(playerId, "LinkedTransferUnit.update", JsonSerializer.Serialize(data));
    }

    private void ClientLog(ulong playerId, string payload)
    {
        var now = DateTime.UtcNow;
        var b = clientLogBudget.AddOrUpdate(playerId, _ => (now, 1),
            (_, v) => now - v.start > TimeSpan.FromMinutes(1) ? (now, 1) : (v.start, v.count + 1));
        if (b.count > 30) return;
        var line = payload ?? "";
        if (line.Length > 4000) line = line.Substring(0, 4000) + "...";
        logger.LogInformation("LinkedTransferUnit: [client {player}] {line}", playerId, line);
    }

    private Task Notify(ulong playerId, string msg) =>
        pub.NotifyTopic(Topics.PlayerNotifications(playerId),
            new NQutils.Messages.PopupReceived(new PopupMessage { message = msg, target = playerId }));

    private Task SendHudEvent(ulong playerId, string eventName, string payload) =>
        pub.NotifyTopic(Topics.PlayerNotifications(playerId),
            new NQutils.Messages.ModTriggerHudEventRequest(new ModTriggerHudEvent
            {
                eventName = eventName,
                eventPayload = payload,
            }));
}
