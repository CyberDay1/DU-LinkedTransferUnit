using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NQ;
using NQ.Grains.Core;
using NQ.Interfaces;
using NQ.RDMS;
using NQutils.Exceptions;
using Orleans;
using Orleans.Runtime;

namespace LinkedTransferUnit
{
    /// Hooks on IndustryUnitGrain so the stock industry panel drives a linked unit.
    ///
    /// The stock grain cannot run this unit: Start needs a recipe ("Recipe not set"), SetRecipe
    /// refuses every item (the grain's transfer-unit branch matches the exact TransferUnit id,
    /// not its children), and with no linked containers a started run only ever jams. So for a
    /// linked unit the hooks replace those calls and keep the state in the element's own
    /// industry "status" property. Every other industry unit is passed straight to the grain.
    public sealed class IndustryHooks
    {
        private readonly IClusterClient orleans;
        private readonly TransferEngine engine;
        private readonly ILogger logger;

        public IndustryHooks(IClusterClient orleans, TransferEngine engine, ILogger logger)
        {
            this.orleans = orleans;
            this.engine = engine;
            this.logger = logger;
        }

        public void Register(IHookCallManager hooks, bool patchStatus)
        {
            hooks.Register("IndustryUnitGrain.Start", HookMode.Replace, this, nameof(Start));
            hooks.Register("IndustryUnitGrain.StopSoft", HookMode.Replace, this, nameof(StopSoft));
            hooks.Register("IndustryUnitGrain.StopHard", HookMode.Replace, this, nameof(StopHard));
            hooks.Register("IndustryUnitGrain.SetRecipe", HookMode.Replace, this, nameof(SetRecipe));
            if (patchStatus)
                hooks.Register("IndustryUnitGrain.Status", HookMode.PostCall, this, nameof(Status));
            // Cheap bonus registration point: a linked unit is known the instant it is placed,
            // not only the instant something calls Start/Stop/SetRecipe/Status on it.
            hooks.Register("ConstructElementsGrain.AddElement", HookMode.PostCall, this, nameof(ElementPlaced));
        }

        private static ulong KeyOf(IIncomingGrainCallContext ctx)
        {
            try
            {
                return ulong.TryParse(((IAddressable)ctx.Grain).GetPrimaryKeyString(), out var id) ? id : 0;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<UnitRef?> Ours(IIncomingGrainCallContext ctx)
        {
            var id = KeyOf(ctx);
            if (id == 0) return null;
            try { return await engine.FindUnit(id); }
            catch (Exception e)
            {
                logger.LogWarning(e, "LinkedTransferUnit: could not tell whether industry {id} is a linked unit; passing the call on", id);
                return null;
            }
        }

        // Replace hooks: (ctx, original arguments...). Anything that is not a linked unit
        // goes to the grain through ctx.Invoke(), never through the grain interface.

        public async Task Start(IIncomingGrainCallContext ctx, ulong playerId, IndustryStart parameters)
        {
            var u = await Ours(ctx);
            if (u == null) { await ctx.Invoke(); return; }
            var asset = new AssetId { type = AssetType.Element, construct = u.ConstructId, element = u.ElementId };
            var rights = await orleans.GetRDMSRightGrain(playerId).GetRightsForPlayerOnAsset(playerId, asset, true);
            if (!rights.rights.Contains(Right.ElementUse) && !rights.rights.Contains(Right.ElementEdit))
                throw new BusinessException(NQ.ErrorCode.Unauthorized, "You do not have the rights to start this unit");
            await engine.SetRunning(u, true);
            logger.LogInformation("LinkedTransferUnit: unit {unit} started by player {player} (stock panel)", u.ElementId, playerId);
        }

        public async Task StopSoft(IIncomingGrainCallContext ctx)
        {
            var u = await Ours(ctx);
            if (u == null) { await ctx.Invoke(); return; }
            await engine.SetRunning(u, false);
            logger.LogInformation("LinkedTransferUnit: unit {unit} stopped (stock panel)", u.ElementId);
        }

        public async Task StopHard(IIncomingGrainCallContext ctx, bool allowIngredientLoss)
        {
            var u = await Ours(ctx);
            if (u == null) { await ctx.Invoke(); return; }
            await engine.SetRunning(u, false);
            logger.LogInformation("LinkedTransferUnit: unit {unit} stopped (stock panel)", u.ElementId);
        }

        public async Task SetRecipe(IIncomingGrainCallContext ctx, ulong recipeId, ulong playerId)
        {
            var u = await Ours(ctx);
            if (u == null) { await ctx.Invoke(); return; }
            // The recipe means nothing to this unit; accept it so the panel lets the player press Start.
            try { await engine.SetRecipeShown(u, recipeId); }
            catch (Exception e) { logger.LogDebug(e, "LinkedTransferUnit: unit {unit} recipe display not updated", u.ElementId); }
        }

        // Post hook: (construct id as a string, the placed element) -> the element, unchanged.
        // Registers a freshly placed linked unit immediately (engine.IsUnitType is a plain
        // HashSet lookup, no I/O), so a Start/Stop/Status call right after placing it is never
        // answered from an empty cache. This fires for every element placed on the whole
        // server, so it must stay this cheap.
        public Task<ElementInfo> ElementPlaced(string constructKey, ElementInfo info)
        {
            if (info != null && engine.IsUnitType(info.elementType))
                engine.RegisterUnit(new UnitRef { ElementId = info.elementId, ConstructId = info.constructId, LocalId = info.localId, TypeId = info.elementType });
            return Task.FromResult(info);
        }

        // Post hook: (grain key, result) -> result. Cache only: no grain calls here.
        public Task<IndustryStatus> Status(string key, IndustryStatus status)
        {
            if (status != null && ulong.TryParse(key, out var id) && engine.IsKnownUnit(id)
                && engine.RunningCache.TryGetValue(id, out var running))
            {
                status.state = running ? IndustryState.RUNNING : IndustryState.STOPPED;
                status.stopRequested = false;
            }
            return Task.FromResult(status);
        }
    }
}
