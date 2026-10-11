using System;

namespace LinkedTransferUnit
{
    /// A route parsed from a unit's element name: "<source-prefix> > <dest-prefix>".
    public sealed class Route
    {
        public string SourcePrefix { get; }
        public string DestPrefix { get; }

        public Route(string source, string dest)
        {
            SourcePrefix = source;
            DestPrefix = dest;
        }

        public override string ToString() => $"{SourcePrefix} > {DestPrefix}";
    }

    /// Pure name logic, no server types, so it is unit-testable.
    public static class Routing
    {
        /// Parse "src > dst". Returns null when the name is not a route
        /// (no separator, more than one separator, or an empty side).
        public static Route? Parse(string? unitName, string separator)
        {
            if (string.IsNullOrWhiteSpace(unitName) || string.IsNullOrEmpty(separator))
                return null;
            var parts = unitName.Split(separator);
            if (parts.Length != 2)
                return null;
            var src = parts[0].Trim();
            var dst = parts[1].Trim();
            if (src.Length == 0 || dst.Length == 0)
                return null;
            return new Route(src, dst);
        }

        /// Tag match: the element name starts with the tag. "outbound-cargo" matches
        /// "outbound-cargo", "outbound-cargo-2", "Outbound-Cargo B" (case-insensitive mode).
        public static bool Matches(string? elementName, string prefix, bool caseInsensitive)
        {
            if (string.IsNullOrEmpty(elementName) || string.IsNullOrEmpty(prefix))
                return false;
            var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return elementName.Trim().StartsWith(prefix, cmp);
        }
    }

    /// Pure quantity maths for one slot move.
    public static class TransferMath
    {
        /// How much free volume a destination offers, in litres.
        /// Fuel tanks stop at fillPercent of their capacity; other containers at capacity.
        public static double FreeVolume(double volume, double maxVolume, bool isFuelTank, double fillPercent)
        {
            if (maxVolume <= 0)
                return 0;
            var pct = isFuelTank ? Math.Clamp(fillPercent, 0, 100) : 100.0;
            var limit = maxVolume * pct / 100.0;
            return Math.Max(0, limit - volume);
        }

        /// Litres to move from a slot: the smallest of the slot, the destination room and the budget.
        public static double LitresToMove(double slotLitres, double destFreeLitres, double budgetLitres)
        {
            var v = Math.Min(slotLitres, Math.Min(destFreeLitres, budgetLitres));
            return v > 0 ? v : 0;
        }

        /// The storage quantity that fits in `litres`, from a slot holding `slotQty` units that
        /// take `slotLitres`. Volume is linear in quantity, so the quantity is proportional; it is
        /// rounded DOWN, so the move never needs more room than was measured. (The server's
        /// GetQuantity(type, litres) rounds to nearest and ignores the item's unit volume, so a
        /// partial move of a 10 L part asked for 10x the room and was always refused.)
        /// Materials count in 1/2^24 L units and items in whole items; both are plain integers here.
        public static long QuantityFor(long slotQty, double slotLitres, double litres)
        {
            if (slotQty <= 0 || litres <= 0) return 0;
            if (slotLitres <= 0 || litres >= slotLitres) return slotQty;
            var q = (long)Math.Floor(slotQty * (litres / slotLitres));
            return Math.Clamp(q, 0, slotQty);
        }
    }
}
