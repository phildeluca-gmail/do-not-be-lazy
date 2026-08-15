using HarmonyLib;
using Verse;

namespace DoNotBeLazy.Core
{
    // Harmony bootstrap. Runs once at game startup and patches everything
    // tagged with [HarmonyPatch] across the assembly.
    [StaticConstructorOnStartup]
    public static class DoNotBeLazyMod
    {
        public const string HarmonyId = "phildeluca.donotbelazy";

        static DoNotBeLazyMod()
        {
            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll();
            Logger.Message("Harmony patches applied.");
        }
    }
}
