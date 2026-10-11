using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LinkedTransferUnit
{
    /// Which client files one art push sends (pure; see AssetPush).
    public static class AssetPlan
    {
        /// The files one push sends, in send order.
        /// Each "ltu_&lt;name&gt;.nqdef" in the folder defines a variant; `variant` picks one. A file
        /// named "ltu_&lt;name&gt;_..." or "ltu_&lt;name&gt;.*" belongs to that variant and is sent only
        /// with it; any other file is shared and always sent. Notes (.md, .txt), .elr files and
        /// dot-files are never sent. Everything sent goes to the client's RAM filesystem.
        public static (string nqdef, List<string> files, List<string> warnings) Plan(IEnumerable<string> fileNames, string variant)
        {
            var warnings = new List<string>();
            var names = fileNames.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var chosen = (variant ?? "").Trim().ToLowerInvariant();
            var variants = names.Where(n => n.StartsWith("ltu_") && n.EndsWith(".nqdef"))
                .Select(n => n.Substring(4, n.Length - 4 - ".nqdef".Length)).ToList();
            string nqdef = variants.Contains(chosen) ? "ltu_" + chosen + ".nqdef" : null;
            if (nqdef == null)
                warnings.Add($"no ltu_{chosen}.nqdef in the client asset folder");
            var files = new List<string>();
            foreach (var n in names)
            {
                var ext = Path.GetExtension(n).ToLowerInvariant();
                if (ext == ".nqdef" || ext == ".md" || ext == ".txt" || ext == ".elr" || n.StartsWith("."))
                    continue;
                var owner = variants.Where(v => n.StartsWith("ltu_" + v + "_") || n.StartsWith("ltu_" + v + "."))
                    .OrderByDescending(v => v.Length).FirstOrDefault();
                if (owner != null && owner != chosen)
                    continue;
                if (n != n.ToLowerInvariant())
                    warnings.Add($"{n} has upper-case letters; NQ warns those may fail to load on the client");
                files.Add(n);
            }
            return (nqdef, files, warnings);
        }
    }
}
