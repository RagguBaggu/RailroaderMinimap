using System;

namespace RailroaderMinimapServer
{
    // The shared core logs through this instead of any loader-specific API
    // (BepInEx's Logger property, UMM's modEntry.Logger, etc.). Each
    // loader's entry point sets these handlers once at startup, pointing
    // them at whatever that loader actually provides.
    internal static class Log
    {
        public static Action<string> InfoHandler;
        public static Action<string> WarningHandler;
        public static Action<string> ErrorHandler;

        public static void Info(string message)
        {
            InfoHandler?.Invoke(message);
        }

        public static void Warning(string message)
        {
            WarningHandler?.Invoke(message);
        }

        public static void Error(string message)
        {
            ErrorHandler?.Invoke(message);
        }
    }
}
