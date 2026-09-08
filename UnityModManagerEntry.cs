using UnityEngine;
using UnityModManagerNet;

namespace RailroaderMinimapServer
{
    public static class UnityModManagerEntry
    {
        private static GameObject _hostObject;

        // Called once by Unity Mod Manager at startup (per Info.json's
        // EntryMethod). Wires the shared core's logging to UMM's own logger
        // and registers OnToggle, which UMM uses to let the mod be safely
        // enabled/disabled at runtime from its mod menu -- without OnToggle,
        // UMM mods can still be turned on, but only turn off on a restart.
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Log.InfoHandler = modEntry.Logger.Log;
            // ModEntry.Logger's public examples consistently show Log(...)
            // and Error(...); a distinct Warning(...) isn't directly
            // confirmed anywhere I could verify, so this assumes it exists
            // by the same convention -- first thing to check if this doesn't
            // compile against the actual UnityModManager.dll.
            Log.WarningHandler = modEntry.Logger.Warning;
            Log.ErrorHandler = modEntry.Logger.Error;

            modEntry.OnToggle = OnToggle;
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value)
            {
                if (_hostObject == null)
                {
                    _hostObject = new GameObject("RailroaderMinimapServer");
                    Object.DontDestroyOnLoad(_hostObject);
                    _hostObject.AddComponent<MinimapServerCore>();
                }
            }
            else if (_hostObject != null)
            {
                Object.Destroy(_hostObject);
                _hostObject = null;
            }

            return true;
        }
    }
}
