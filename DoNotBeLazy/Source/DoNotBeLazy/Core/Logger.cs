namespace DoNotBeLazy.Core
{
    // Named Logger (not Log) to avoid colliding with Verse.Log when both
    // namespaces are in scope. VerboseLogging will be wired to
    // DoNotBeLazySettings once that exists (Phase 2).
    public static class Logger
    {
        public static bool VerboseLogging = false;

        private const string Prefix = "[DoNotBeLazy] ";

        public static void Message(string text)
        {
            if (VerboseLogging)
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
