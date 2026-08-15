using RimWorld;
using Verse;

namespace DoNotBeLazy.Utility
{
    // Central "can this pawn take part in a sweep of this work type" check.
    // Reused by SweepManager (Phase 3) both when a sweep starts and on
    // every tick while a pawn remains in an active sweep, per the
    // death/downed/mental-break edge case in the architecture doc.
    //
    // Per-bill skill requirements (Bill.recipe.skillRequirements) are a
    // workstation-specific concern handled where bills are selected, not
    // here - this checks general pawn eligibility for a work type.
    public static class PawnValidator
    {
        public static bool CanSweep(Pawn pawn, WorkGiverDef workGiverDef)
        {
            if (pawn == null || workGiverDef?.workType == null)
            {
                return false;
            }

            if (pawn.Dead || pawn.Downed || pawn.InMentalState || pawn.Drafted)
            {
                return false;
            }

            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
            {
                return false;
            }

            WorkTypeDef workType = workGiverDef.workType;

            if (pawn.WorkTypeIsDisabled(workType))
            {
                return false;
            }

            if (!pawn.workSettings.WorkIsActive(workType))
            {
                return false;
            }

            if (pawn.health?.capacities == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return false;
            }

            return true;
        }
    }
}
