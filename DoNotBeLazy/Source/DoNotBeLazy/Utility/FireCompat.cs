using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // Same job as GrowerCompat, different WorkGiver: the bits we have to do
    // by hand because the mod calls HasJobOnThing directly, plus the one
    // vanilla check we deliberately drop.
    //
    // Firefighting was unreachable from this mod for three separate reasons,
    // all of them deliberate when they were written:
    //   - FightFires is directOrderable:false, and IsSweepEligible respects
    //     that flag generally (that's why vanilla has no "put out fire"
    //     right-click option either)
    //   - FloatMenuPatch bailed out of the whole right-click on a burning
    //     cell, since nothing else we offer makes sense there
    //   - TaskScanner.TargetIsBurning drops every burning target, and a Fire
    //     is the burning thing
    // All three now check IsFirefighting first.
    //
    // The big one: vanilla refuses any fire outside the home area (that's
    // what keeps auto-firefighting from marching colonists into a wildfire).
    // Overridden here per explicit decision - the player is clicking the
    // fire, so they mean it. JobOnThing has no gate of its own (its whole
    // body is "return new Job(JobDefOf.BeatFire, t)"), so the override is
    // just a matter of not calling HasJobOnThing.
    public static class FireCompat
    {
        // reservation check only kicks in past this in vanilla
        private const float ReserveCheckDistSquared = 225f;

        // a fire with a reserver already standing this close counts as
        // handled - it's what makes a group fan out over separate fires
        private const float HandledDistSquared = 25f;

        // keyed on work type, not the FightFires defName, so a modded
        // firefighting WorkGiver gets the same treatment
        public static bool IsFirefighting(WorkGiverDef def)
        {
            return def?.workType != null && def.workType.defName == "Firefighter";
        }

        // Stands in for WorkGiver_FightFires.HasJobOnThing. Keeps everything
        // vanilla checks except the home-area gate. Can't subclass or call
        // into the real thing - WorkGiver_FightFires is internal, so even
        // its public static FireIsBeingHandled is out of reach from here.
        public static bool HasFireJob(Pawn pawn, Thing thing, bool forced)
        {
            if (!(thing is Fire fire) || pawn == null)
            {
                return false;
            }

            // fire burning ON a pawn - beating out your own is not a thing
            if (fire.parent is Pawn burning)
            {
                if (burning == pawn)
                {
                    return false;
                }
                if (!pawn.CanReach(burning, PathEndMode.Touch, Danger.Deadly))
                {
                    return false;
                }

                // vanilla also refuses a same-faction pawn burning outside
                // the home area more than 15 tiles off. That's the same
                // home-area gate we're overriding, so it's gone.
            }
            else
            {
                // on Pawn itself in 1.5, not on story - the decompile on
                // github is an older build and has pawn.story.WorkTagIsDisabled
                if (pawn.WorkTagIsDisabled(WorkTags.Firefighting))
                {
                    return false;
                }

                // this is where vanilla checks
                // pawn.Map.areaManager.Home[fire.Position] and bails.
                // Deliberately not doing that - see the class comment.
            }

            if ((pawn.Position - fire.Position).LengthHorizontalSquared > ReserveCheckDistSquared
                && !pawn.CanReserve(fire, 1, -1, null, forced))
            {
                return false;
            }

            if (!pawn.CanReach(fire, PathEndMode.Touch, Danger.Deadly))
            {
                return false;
            }

            return !FireIsBeingHandled(fire, pawn);
        }

        private static bool FireIsBeingHandled(Fire fire, Pawn potentialHandler)
        {
            if (!fire.Spawned)
            {
                return false;
            }

            // 3-arg in this build (the decompile on github shows 2) - layer
            // is null, same as vanilla passes
            Pawn reserver = fire.Map.reservationManager.FirstRespectedReserver(fire, potentialHandler, null);
            if (reserver == null)
            {
                return false;
            }

            return (reserver.Position - fire.Position).LengthHorizontalSquared <= HandledDistSquared;
        }
    }
}
