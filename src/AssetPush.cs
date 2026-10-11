using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backend;
using Microsoft.Extensions.Logging;
using NQ;
using NQutils;

namespace LinkedTransferUnit
{
    /// Sends the unit's own element art to a connecting client, using NQ's documented
    /// resource-loading protocol (repo dual-universe/mydu-server-mods, ModLoader/README.md):
    /// a VoxelCsgApplied with constructId 0 and
    ///   flags 1 = load an .nqdef (JSON in "operation"),
    ///   flags 2 = put a file into the client's Unigine RAM filesystem (bytes in "operation",
    ///             path in "hashContext"),
    ///   flags 3 = element (re)load request (element name in "hashContext").
    /// This is the "raw" mode of NQ's sample loader. The cached/synchronous modes route the
    /// client's reply to a mod literally named "Loader"/"SyncLoader", so this mod does not use
    /// them: it can then run beside a server's own loader without a name clash.
    public sealed class AssetPush
    {
        public const string ResourcePrefix = "resources_generated/";

        private readonly IPub pub;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<ulong, DateTime> lastSent = new();

        public AssetPush(IPub pub, ILogger logger)
        {
            this.pub = pub;
            this.logger = logger;
        }

        /// Sends the art to one player. Throttled so a client asking for mod info twice in a
        /// row gets one push.
        public async Task SendTo(ulong playerId, LtuConfig cfg, bool force = false)
        {
            if (playerId == 0 || !cfg.ClientAssets)
                return;
            var now = DateTime.UtcNow;
            if (!force && lastSent.TryGetValue(playerId, out var last) && (now - last).TotalSeconds < cfg.ClientAssetMinResendSeconds)
                return;
            lastSent[playerId] = now;

            var dir = cfg.ClientAssetDir;
            if (!Directory.Exists(dir))
            {
                logger.LogWarning("LinkedTransferUnit: client asset folder {dir} missing; clients keep the stock look", dir);
                return;
            }
            var (nqdef, files, warnings) = AssetPlan.Plan(Directory.GetFiles(dir), cfg.AssetVariant);
            foreach (var w in warnings)
                logger.LogWarning("LinkedTransferUnit: client assets: {warning}", w);
            if (nqdef == null)
                return;

            // NQ's order: definitions, then files, then the element load request.
            await Send(playerId, new VoxelEdit { flags = 1, operation = File.ReadAllBytes(Path.Combine(dir, nqdef)) });
            await Task.Delay(1000);
            long bytes = 0;
            foreach (var f in files)
            {
                var content = File.ReadAllBytes(Path.Combine(dir, f));
                bytes += content.Length;
                await Send(playerId, new VoxelEdit { flags = 2, operation = content, hashContext = ResourcePrefix + f });
            }
            await Task.Delay(1000);
            await Send(playerId, new VoxelEdit { flags = 3, hashContext = cfg.ItemName });
            logger.LogInformation("LinkedTransferUnit: sent client art to player {player}: variant {variant} ({nqdef}), {count} file(s), {bytes} bytes, element load {element}",
                playerId, cfg.AssetVariant, nqdef, files.Count, bytes, cfg.ItemName);
        }

        private Task Send(ulong playerId, VoxelEdit edit) =>
            pub.NotifyTopic(Topics.PlayerNotifications(playerId), new NQutils.Messages.VoxelCsgApplied(edit));
    }
}
