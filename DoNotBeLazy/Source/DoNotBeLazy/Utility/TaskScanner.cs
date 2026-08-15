using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // Finds candidate task targets for an area sweep. SweepManager (Phase 3)
    // calls this once per sweep initiation and caches the result rather than
    // rescanning every tick - GenRadial at radius 16 touches ~800 cells.
    //
    // forPawn drives the scan: WorkGiver_Scanner.PotentialWorkThingsGlobal
    // and HasJobOnThing are inherently pawn-scoped vanilla APIs. Any single
    // sweep-eligible pawn works as the driver for building the shared
    // candidate list; SweepManager still re-checks CanReserve per assignee
    // at the moment it actually claims a target for a *different* pawn.
    //
    // Caveat: a handful of WorkGiver_Scanner subclasses override
    // PotentialWorkThingsGlobal with custom iteration that may not respect
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

            // PotentialWorkThingsGlobal returns NULL on the base class (its
            // IL is literally ldnull/ret) and most WorkGivers never override
            // it - construction and bills included. Iterating that straight
            // was an NRE. Vanilla's JobGiver_Work does the same thing we do
            // here: fall back to the thing lister for scanThings givers.
            // Cell-only givers (scanCells, e.g. clear snow) find nothing,
            // which is the intended no-op rather than a crash.
            IEnumerable<Thing> candidates = scanner.PotentialWorkThingsGlobal(forPawn);
            if (candidates == null && workGiverDef.scanThings)
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
                return results;
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

                if (!scanner.HasJobOnThing(forPawn, thing))
                {
                    continue;
                }

                results.Add(thing);
            }

            return results;
        }
    }
}
