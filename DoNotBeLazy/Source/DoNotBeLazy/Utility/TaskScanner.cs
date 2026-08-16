using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // Finds candidate task targets for an area sweep. SweepManager calls
    // this once per sweep initiation and caches the result rather than
    // rescanning every tick.
    //
    // forPawn drives the scan: WorkGiver_Scanner.PotentialWorkThingsGlobal,
    // HasJobOnThing/HasJobOnCell are inherently pawn-scoped vanilla APIs.
    // Any single sweep-eligible pawn works as the driver for building the
    // shared candidate list; SweepManager still re-checks CanReserve per
    // assignee at the moment it actually claims a target for a *different*
    // pawn.
    //
    // Caveat: a handful of WorkGiver_Scanner subclasses override the
    // Potential*Global methods with custom iteration that may not respect
    // the pawn-scoping assumptions here. Worst case for those is an empty
    // result (the * command finds nothing), not a crash - verify in-game
    // per WorkGiverDef as sweep types are added.
    public static class TaskScanner
    {
        public static List<LocalTargetInfo> FindTargets(IntVec3 center, int radius, Map map, WorkGiverDef workGiverDef, Pawn forPawn)
        {
            var results = new List<LocalTargetInfo>();

            if (map == null || forPawn == null)
            {
                return results;
            }

            if (!(workGiverDef?.Worker is WorkGiver_Scanner scanner))
            {
                return results;
            }

            float radiusSquared = radius * radius;
            Area allowedArea = forPawn.playerSettings?.AreaRestrictionInPawnCurrentMap;

            // scanCells and scanThings aren't mutually exclusive on the def,
            // so both branches can contribute to the same pool
            if (workGiverDef.scanCells)
            {
                ScanCells(center, radius, radiusSquared, map, scanner, forPawn, allowedArea, results);
            }

            if (workGiverDef.scanThings)
            {
                ScanThings(center, radiusSquared, map, scanner, forPawn, allowedArea, results);
            }

            return results;
        }

        // No global lister for "empty farmable cells" the way listerThings
        // covers things, so this is a real radial scan - GrowerSow (sow
        // crops) is the case that needs it: scanCells=true, scanThings=false,
        // and the target cell has nothing on it (that's the whole point -
        // it's empty farmland waiting for a seed).
        private static void ScanCells(IntVec3 center, int radius, float radiusSquared, Map map, WorkGiver_Scanner scanner, Pawn forPawn, Area allowedArea, List<LocalTargetInfo> results)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map))
                {
                    continue;
                }

                if ((cell - center).LengthHorizontalSquared > radiusSquared)
                {
                    continue;
                }

                if (allowedArea != null && !allowedArea[cell])
                {
                    continue;
                }

                if (!map.reservationManager.CanReserve(forPawn, cell))
                {
                    continue;
                }

                // forced:true - matches how a manually-issued order behaves
                // in vanilla (e.g. bypasses ignoreOtherReservations-style
                // conflicts that only block the AI's own automatic scan)
                if (!scanner.HasJobOnCell(forPawn, cell, true))
                {
                    continue;
                }

                results.Add(cell);
            }
        }

        // PotentialWorkThingsGlobal returns NULL on the base class (its IL
        // is literally ldnull/ret) and most WorkGivers never override it -
        // construction and bills included. Iterating that straight was an
        // NRE. Vanilla's JobGiver_Work does the same thing we do here: fall
        // back to the thing lister.
        private static void ScanThings(IntVec3 center, float radiusSquared, Map map, WorkGiver_Scanner scanner, Pawn forPawn, Area allowedArea, List<LocalTargetInfo> results)
        {
            IEnumerable<Thing> candidates = scanner.PotentialWorkThingsGlobal(forPawn);
            if (candidates == null)
            {
                ThingRequest req = scanner.PotentialWorkThingRequest;
                // ThingsMatching throws on an undefined request
                if (!req.IsUndefined)
                {
                    candidates = map.listerThings.ThingsMatching(req);
                }
            }

            if (candidates == null)
            {
                return;
            }

            foreach (Thing thing in candidates)
            {
                if (thing == null || thing.Map != map)
                {
                    continue;
                }

                if ((thing.Position - center).LengthHorizontalSquared > radiusSquared)
                {
                    continue;
                }

                if (allowedArea != null && !allowedArea[thing.Position])
                {
                    continue;
                }

                if (thing.IsForbidden(forPawn))
                {
                    continue;
                }

                if (!map.reservationManager.CanReserve(forPawn, thing))
                {
                    continue;
                }

                if (!scanner.HasJobOnThing(forPawn, thing, true))
                {
                    continue;
                }

                results.Add(thing);
            }
        }
    }
}
