using Railroader.Modding;
using UnityEngine;

namespace RailroaderMinimapServer
{
    // Entry point for Railroader's built-in mod loader (2026.1+). The game
    // reads Info.json's "assemblies" list, loads each DLL, and instantiates
    // every public non-abstract IModEntryPoint it finds -- this class. It
    // only runs DLL mods at all when the player has turned on Preferences >
    // "Allow Unsafe DLL Mods".
    //
    // Mods can be unloaded and reloaded mid-session (e.g. when the active mod
    // set changes to match a save), and the reload calls OnModLoad on a fresh
    // instance immediately after OnModUnload on the old one -- so unloading
    // must release the server's ports synchronously, not at end of frame.
    public sealed class GameModEntry : IModEntryPoint, IWorldLifecycle
    {
        private GameObject _hostObject;
        private MinimapServerCore _core;

        public void OnModLoad(IModContext context)
        {
            IModLogger logger = context.Log;
            Log.InfoHandler = logger.Info;
            Log.WarningHandler = logger.Warning;
            Log.ErrorHandler = logger.Error;

            _hostObject = new GameObject("RailroaderMinimapServer");
            Object.DontDestroyOnLoad(_hostObject);
            _core = _hostObject.AddComponent<MinimapServerCore>();
        }

        public void OnModUnload()
        {
            if (_hostObject != null)
            {
                // DestroyImmediate, not Destroy: Destroy is deferred to the end
                // of the frame, which would leave this instance's listeners
                // holding ports 8080/8081 while a reload's new instance tries
                // to bind them in the same frame. Safe here since this is the
                // mod's own object, not one the game is iterating over.
                Object.DestroyImmediate(_hostObject);
            }
            _hostObject = null;
            _core = null;
        }

        public void OnWorldLoad()
        {
            if (_core != null) _core.OnWorldLoaded();
        }

        public void OnWorldUnload()
        {
            if (_core != null) _core.OnWorldUnloaded();
        }
    }
}
