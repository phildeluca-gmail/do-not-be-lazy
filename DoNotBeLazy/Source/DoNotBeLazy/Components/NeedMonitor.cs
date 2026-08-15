using RimWorld;
using Verse;
using Verse.AI;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Components
{
    // GameComponent: every 60 ticks, checks pawns currently in an active
    // sweep for critical needs (hunger, recreation, sleep). If any need is
    // at or below DoNotBeLazySettings.needThreshold, the pawn's sweep is
    // cancelled and their forced job is ended so vanilla AI takes over.
    // Per the architecture doc, they do NOT rejoin the sweep automatically
    // afterward - this avoids interrupt loops; the player re-issues if
    // wanted.
    //
    // RimWorld auto-instantiates every non-abstract GameComponent subclass
    // with a (Game) constructor when a game is created/loaded, so this
    // needs no manual registration.
    //
    // Depends on SweepManager (Phase 3, per-map MapComponent), which must
    // expose:
    //   IEnumerable<Pawn> GetSweptPawns()
    //   void RemoveSweep(Pawn pawn)
    // Will not compile until that exists.
    public class NeedMonitor : GameComponent
    {
        private const int CheckIntervalTicks = 60;

        public NeedMonitor(Game game)
        {
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
            {
                return;
            }

            float threshold = DoNotBeLazyMod.Settings.needThreshold;

            foreach (Map map in Find.Maps)
            {
                SweepManager sweepManager = map.GetComponent<SweepManager>();
                if (sweepManager == null)
                {
                    continue;
                }

                foreach (Pawn pawn in sweepManager.GetSweptPawns())
                {
                    if (!NeedIsCritical(pawn, threshold))
                    {
                        continue;
                    }

                    sweepManager.RemoveSweep(pawn);

                    if (pawn.jobs?.curJob != null)
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }

                    Logger.Message($"{pawn.LabelShort} pulled from sweep: need at/below threshold.");
                }
            }
        }

        private static bool NeedIsCritical(Pawn pawn, float threshold)
        {
            return NeedIsCritical(pawn.needs?.food, threshold)
                || NeedIsCritical(pawn.needs?.joy, threshold)
                || NeedIsCritical(pawn.needs?.rest, threshold);
        }

        private static bool NeedIsCritical(Need need, float threshold)
        {
            return need != null && need.CurLevelPercentage <= threshold;
        }
    }
}
