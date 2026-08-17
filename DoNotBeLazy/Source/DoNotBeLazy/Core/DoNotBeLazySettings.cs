using UnityEngine;
using Verse;

namespace DoNotBeLazy.Core
{
    // Per architecture doc 3.4: sweep radius, need-interrupt threshold,
    // and whether to draw the radius overlay on hover.
    public class DoNotBeLazySettings : ModSettings
    {
        public int sweepRadius = 16;
        public float needThreshold = 0.05f;
        public float moodThreshold = 0.10f;
        public bool showSweepOverlay = true;
        public bool verboseLogging = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref sweepRadius, "sweepRadius", 16);
            Scribe_Values.Look(ref needThreshold, "needThreshold", 0.05f);
            Scribe_Values.Look(ref moodThreshold, "moodThreshold", 0.10f);
            Scribe_Values.Look(ref showSweepOverlay, "showSweepOverlay", true);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);

            // the toggle only exists to drive this - keep them in sync on
            // load as well as on change, or a saved "on" reads as off until
            // the settings window is opened
            Logger.VerboseLogging = verboseLogging;
        }

        public void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label($"Sweep radius: {sweepRadius} tiles");
            sweepRadius = Mathf.RoundToInt(listing.Slider(sweepRadius, 1f, 50f));

            listing.Gap();
            listing.Label($"Need interrupt threshold: {needThreshold:P0}");
            needThreshold = listing.Slider(needThreshold, 0.01f, 0.20f);

            listing.Gap();
            listing.Label($"Mood interrupt threshold: {moodThreshold:P0}");
            moodThreshold = listing.Slider(moodThreshold, 0.01f, 0.30f);

            listing.Gap();
            listing.CheckboxLabeled("Show sweep radius overlay on hover", ref showSweepOverlay);

            listing.Gap();
            listing.CheckboxLabeled("Verbose logging (for bug reports)", ref verboseLogging,
                "Writes a [DoNotBeLazy] trace of every sweep decision to the log. Leave off for normal play.");
            Logger.VerboseLogging = verboseLogging;

            listing.End();
        }
    }
}
