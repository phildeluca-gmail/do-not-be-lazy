using HarmonyLib;
using UnityEngine;
using Verse;

namespace DoNotBeLazy.Core
{
    // Mod entry point. RimWorld constructs one instance of this per load,
    // passing the ModContentPack. Applies Harmony patches and hosts the
    // in-game settings UI (Options > Mod Settings > Do Not Be Lazy).
    public class DoNotBeLazyMod : Mod
    {
        public const string HarmonyId = "phildeluca.donotbelazy";

        public static DoNotBeLazySettings Settings;

        public DoNotBeLazyMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DoNotBeLazySettings>();

            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll();
            Logger.Message("Harmony patches applied.");
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.DoWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "Do Not Be Lazy";
        }
    }
}
