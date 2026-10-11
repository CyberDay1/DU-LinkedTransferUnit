using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LinkedTransferUnit
{
    /// Server-admin settings, read from ModLinkedTransferUnit.json next to the DLL.
    /// Every field has a default, so a missing file or a missing key is fine.
    public sealed class LtuConfig
    {
        // ---- Item ----
        /// Item-bank name of the linked transfer unit. Only elements of this type (or its
        /// children) are treated as linked transfer units.
        public string ItemName { get; set; } = "LinkedTransferUnit";
        /// On boot, add the item to the item bank if it is missing.
        public bool CreateItemIfMissing { get; set; } = true;
        /// Parent class of the created item; also used for its look via AssetAlias.
        public string ItemParent { get; set; } = "TransferUnit";
        /// Reuse this item's art for the new item ("alias") when ClientAssets is off. With
        /// ClientAssets on, the item's assetAlias is cleared so clients look up the
        /// "LinkedTransferUnit" element definition this mod sends them. Synced at boot.
        public string ItemAssetAlias { get; set; } = "TransferUnit";

        // ---- Client art ----
        /// Send the unit's own element definition and art to every connecting client
        /// (NQ's resource-loading protocol, raw mode). Off = the stock transfer unit look.
        public bool ClientAssets { get; set; } = true;
        /// Folder holding the client files (flat, lower-case names).
        public string ClientAssetDir { get; set; } = "/OrleansGrains/Mods/LinkedTransferUnit/client";
        /// Which look to send: the file ltu_<variant>.nqdef in ClientAssetDir. "kit" = stock
        /// meshes combined, "mesh" = the custom exported mesh. Read at every login.
        public string AssetVariant { get; set; } = "kit";
        /// A player is sent the art at most once in this many seconds.
        public double ClientAssetMinResendSeconds { get; set; } = 20;

        /// The assetAlias the item should carry for the current settings.
        public string EffectiveAssetAlias => ClientAssets ? "" : (ItemAssetAlias ?? "");
        public string ItemDisplayName { get; set; } = "Linked Transfer Unit";
        public string ItemDescription { get; set; } =
            "Moves items between containers within range, without links. Pair containers with Linked Transfer Unit > Pair source / Pair destination, or set name prefixes in Linked Transfer Unit > Configure, then press Start.";

        // ---- Routing (OPEN: defaults chosen pending the server owner's ruling) ----
        /// Separator between source and destination tag in the unit's name.
        public string RouteSeparator { get; set; } = ">";
        /// Tag prefixes match container names case-insensitively.
        public bool CaseInsensitive { get; set; } = true;
        /// A unit with no saved panel config may still take its route from its name
        /// ("source > destination"), as before the config panel existed.
        public bool AllowNameRoutes { get; set; } = true;
        /// Longest prefix the config panel accepts, in characters.
        public int MaxPrefixLength { get; set; } = 64;
        /// Where the per-unit config (prefixes, pairs, running state) is stored (JSON, keyed by element id).
        public string StorePath { get; set; } = "/OrleansGrains/Mods/LinkedTransferUnit/units.json";

        /// Hours a saved unit config is kept after the unit disappears, so a unit placed back
        /// or redeployed from a blueprint can pick it up again by its fallback key.
        public double UnitConfigKeepHours { get; set; } = 168;

        // ---- Pairing ----
        /// Paired sources per unit, and paired destinations per unit (each).
        public int MaxPairsPerRole { get; set; } = 16;
        /// Seconds a unit stays in pairing mode after Pair source / Pair destination, and
        /// after each successful pair.
        public double PairingWindowSeconds { get; set; } = 60;
        /// Seconds between fallback lookups for a pair whose element id is gone.
        public double PairRetrySeconds { get; set; } = 30;

        // ---- Running state ----
        /// Move items only while the unit's industry state is RUNNING. false = every unit
        /// with a route runs, as before.
        public bool RequireRunning { get; set; } = true;
        /// Hook IndustryUnitGrain Start/StopSoft/StopHard/SetRecipe so the stock industry
        /// panel's Start and Stop toggle this unit. Other industry units pass straight through.
        public bool HookStockStartStop { get; set; } = true;
        /// Also hook IndustryUnitGrain.Status so the stock panel reads RUNNING/STOPPED from
        /// the same flag. The server logs one "intercepting" line per hooked call.
        public bool PatchIndustryStatus { get; set; } = true;

        // ---- Config panel ----
        /// Log the stock industry panel's DOM, styles and the HUD events around its opening once
        /// per client session (to the server log), to check the tab injection against.
        public bool ProbeStockPanel { get; set; } = true;
        /// Send the panel script at login so the stock industry panel of a linked unit shows the
        /// mod's tabs (LINKS, FILTER, STATUS) in place of its own.
        public bool InjectStockPanel { get; set; } = true;
        /// When the script is (re)sent after login, until the client confirms it loaded.
        public List<double> InjectDelaysSeconds { get; set; } = new() { 3, 8, 15, 30, 60, 120 };
        /// Fallback when the client cannot say which unit the stock panel shows: the industry
        /// unit nearest the player, if it is a linked unit within this many metres.
        public double StockPanelMatchMeters { get; set; } = 12;

        // ---- Item filter ----
        /// Most entries (items or groups) in one unit's filter.
        public int MaxFilterEntries { get; set; } = 200;
        /// Item-bank node whose subtree the item picker offers.
        public string CatalogRoot { get; set; } = "BaseItem";
        /// Subtrees left out of the picker (by item-bank name). Data items are instanced, so
        /// the unit never moves them; Tool and MissionPackageContentAphelia (displayed as
        /// "Tools" and "Aphelia Mission Packages") are neither containable cargo nor things a
        /// transfer unit moves, and the user asked for them gone from the picker (2026-10-09).
        public List<string> CatalogExclude { get; set; } = new() { "DataItem", "Tool", "MissionPackageContentAphelia" };
        /// Most items in one picker page (a group's items), and in one search result.
        public int CatalogBrowseMax { get; set; } = 400;
        public int CatalogSearchMax { get; set; } = 120;

        // ---- Range and pacing ----
        /// Hard cap, metres, element to element.
        public double MaxRangeMeters { get; set; } = 1000;
        /// Seconds between transfer cycles.
        public double CycleSeconds { get; set; } = 5;
        /// Per-unit throughput cap per cycle, litres.
        public double MaxLitresPerUnitPerCycle { get; set; } = 1000;
        /// Global cap: units processed per cycle (round-robin beyond this).
        public int MaxUnitsPerCycle { get; set; } = 200;
        /// Seconds between database rescans for placed units.
        public double RescanSeconds { get; set; } = 30;
        /// Seconds a construct's container list is cached.
        public double ContainerCacheSeconds { get; set; } = 15;

        // ---- Fuel (OPEN) ----
        /// Fuel tanks fill to this percent of capacity.
        public double FuelFillPercent { get; set; } = 100;
        /// Only move fuel whose family matches the tank's fuelType.
        public bool RequireMatchingFuelType { get; set; } = true;
        /// Tank fuelType -> item-bank fuel family.
        public Dictionary<string, string> FuelTypeFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Atmospheric"] = "AtmoFuel",
            ["Space"] = "SpaceFuel",
            ["Rocket"] = "RocketFuel",
        };

        // ---- Rights (OPEN) ----
        /// Allow containers owned by someone else when RDMS grants the unit owner the rights.
        /// false = the container's construct must have the same owner as the unit.
        public bool AllowOtherOwnersWithRights { get; set; } = true;

        // ---- Items ----
        /// Move only plain stackable items (no per-instance properties).
        public bool SkipInstancedItems { get; set; } = true;

        // ---- Item safety ----
        /// Seconds after which an in-flight journal entry written by this server instance counts
        /// as interrupted (entries from an earlier instance are resolved at once).
        public double JournalStaleSeconds { get; set; } = 120;
        /// DEBUG ONLY. Lets an admin arm injected faults (mod action 212) for the test harness.
        /// Read from the file each time a fault is armed, so it can be flipped without a restart.
        public bool FaultInjection { get; set; } = false;

        // ---- Logging ----
        /// Log every move (Information). Otherwise only a per-cycle summary when something moved.
        public bool LogEveryMove { get; set; } = true;
        /// Minutes between "still running" heartbeat lines.
        public double HeartbeatMinutes { get; set; } = 10;

        public static string DefaultPath =>
            Environment.GetEnvironmentVariable("LTU_CONFIG") ?? "/OrleansGrains/Mods/ModLinkedTransferUnit.json";

        public static (LtuConfig cfg, string source) Load(string path)
        {
            if (!File.Exists(path))
                return (new LtuConfig(), "defaults (no config file at " + path + ")");
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var cfg = JsonSerializer.Deserialize<LtuConfig>(File.ReadAllText(path), opts) ?? new LtuConfig();
            cfg.Sanitize();
            return (cfg, path);
        }

        public void Sanitize()
        {
            if (CycleSeconds < 1) CycleSeconds = 1;
            if (RescanSeconds < 5) RescanSeconds = 5;
            if (ContainerCacheSeconds < 0) ContainerCacheSeconds = 0;
            if (MaxRangeMeters < 0) MaxRangeMeters = 0;
            if (MaxLitresPerUnitPerCycle < 0) MaxLitresPerUnitPerCycle = 0;
            if (MaxUnitsPerCycle < 1) MaxUnitsPerCycle = 1;
            FuelFillPercent = Math.Clamp(FuelFillPercent, 0, 100);
            if (string.IsNullOrEmpty(RouteSeparator)) RouteSeparator = ">";
            MaxPrefixLength = Math.Clamp(MaxPrefixLength, 1, 256);
            MaxPairsPerRole = Math.Clamp(MaxPairsPerRole, 0, 256);
            PairingWindowSeconds = Math.Clamp(PairingWindowSeconds, 5, 3600);
            if (PairRetrySeconds < 0) PairRetrySeconds = 0;
            if (UnitConfigKeepHours < 0.1) UnitConfigKeepHours = 0.1;
            if (string.IsNullOrWhiteSpace(StorePath)) StorePath = "/OrleansGrains/Mods/LinkedTransferUnit/units.json";
            if (string.IsNullOrWhiteSpace(ClientAssetDir)) ClientAssetDir = "/OrleansGrains/Mods/LinkedTransferUnit/client";
            if (string.IsNullOrWhiteSpace(AssetVariant)) AssetVariant = "kit";
            if (ClientAssetMinResendSeconds < 0) ClientAssetMinResendSeconds = 0;
            MaxFilterEntries = Math.Clamp(MaxFilterEntries, 1, 5000);
            CatalogBrowseMax = Math.Clamp(CatalogBrowseMax, 20, 2000);
            CatalogSearchMax = Math.Clamp(CatalogSearchMax, 10, 1000);
            if (string.IsNullOrWhiteSpace(CatalogRoot)) CatalogRoot = "BaseItem";
            CatalogExclude ??= new();
            if (InjectDelaysSeconds == null || InjectDelaysSeconds.Count == 0) InjectDelaysSeconds = new() { 3, 8, 15, 30, 60, 120 };
            if (StockPanelMatchMeters < 0) StockPanelMatchMeters = 0;
            if (JournalStaleSeconds < 10) JournalStaleSeconds = 10;
            FuelTypeFamilies = new Dictionary<string, string>(FuelTypeFamilies ?? new(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
