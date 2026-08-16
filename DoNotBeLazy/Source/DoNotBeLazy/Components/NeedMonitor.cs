using RimWorld;
using Verse;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Components
{
    // GameComponent: every 60 ticks, checks pawns currently in an active
    // sweep for critical needs (hunger, recreation, sleep - vs needThreshold;
    // mood has its own separate moodThreshold, since 5% mood is basically a
    // mental break already and the player asked for a higher default there).
    // If any is critical, the pawn's forced job is ended so vanilla AI takes
    // over - EndCurrentJob defaults to startNewJob:true, so the pawn's
    // normal think tree picks a new job immediately, which for hunger/rest/
    // joy already means "go address it" without us issuing anything
    // explicit. Mood doesn't have a single fix-it job in vanilla; letting
    // the think tree take over is the only lever there too.
    //
    // The sweep itself is paused, not cancelled (SweepManager.PauseForNeed,
    // not RemoveSweep) - per explicit user request, once the pawn's own
    // need-driven job finishes on its own they resume the last-ordered
    // work. This reverses the original design (see architecture doc
    // section 2 history), which cancelled outright to avoid interrupt
    // loops; pausing avoids the same loop risk since resumption only
    // happens on a real job-end event, not a repeated need check.
    //
    // RimWorld auto-instantiates every non-abstract GameComponent subclass
    // with a (Game) constructor when a game is created/loaded, so this
    // needs no manual registration.
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
            float moodThreshold = DoNotBeLazyMod.Settings.moodThreshold;

            foreach (Map map in Find.Maps)
            {
                SweepManager sweepManager = map.GetComponent<SweepManager>();
                if (sweepManager == null)
                {
                    continue;
                }

                foreach (Pawn pawn in sweepManager.GetSweptPawns())
                {
                    // already pending on a need from an earlier tick -
                    // don't re-pause every 60 ticks while they sleep it off
                    if (sweepManager.IsPaused(pawn))
                    {
                        continue;
                    }

                    if (!NeedIsCritical(pawn, threshold, moodThreshold))
                    {
                        continue;
                    }

                    sweepManager.PauseForNeed(pawn);
                    Logger.Message($"{pawn.LabelShort} paused from sweep: need at/below threshold. Will resume once addressed.");
                }
            }
        }

        private static bool NeedIsCritical(Pawn pawn, float threshold, float moodThreshold)
        {
            return NeedIsCritical(pawn.needs?.food, threshold)
                || NeedIsCritical(pawn.needs?.joy, threshold)
                || NeedIsCritical(pawn.needs?.rest, threshold)
                || NeedIsCritical(pawn.needs?.mood, moodThreshold);
        }

        private static bool NeedIsCritical(Need need, float threshold)
        {
            return need != null && need.CurLevelPercentage <= threshold;
        }
    }
}
