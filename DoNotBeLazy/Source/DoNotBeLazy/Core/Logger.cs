namespace DoNotBeLazy.Core
{
    // Named Logger (not Log) to avoid colliding with Verse.Log when both
    // namespaces are in scope.
    //
    // VerboseLogging is driven by the settings checkbox - set from
    // DoNotBeLazySettings on both ExposeData (load) and the toggle itself.
    // It sat hardcoded false from Phase 1 until 2026-08-16, which silently
    // made every Logger.Message call in the mod a no-op: several live
    // playtest logs showed zero DoNotBeLazy lines and were read as "nothing
    // fired" when the truth was "nothing could be printed".
    public static class Logger
    {
        public static bool VerboseLogging = false;

        // Separate switch from VerboseLogging on purpose - job-pipeline
        // diagnostics and the sweep trace answer different questions and
        // you rarely want both walls of text at once.
        public static bool JobDiagnostics = false;

        private const string Prefix = "[DoNotBeLazy] ";

        public static void Message(string text)
        {
            if (VerboseLogging)
            {
                Verse.Log.Message(Prefix + text);
            }
        }

        // same prefix as everything else so one extraction still catches
        // the lot - the pull-logs command splits them apart afterwards
        public static void Diag(string text)
        {
            if (JobDiagnostics)
            {
                Verse.Log.Message(Prefix + text);
            }
        }

        public static void Warning(string text)
        {
            Verse.Log.Warning(Prefix + text);
        }

        public static void Error(string text)
        {
            Verse.Log.Error(Prefix + text);
        }
    }
}
