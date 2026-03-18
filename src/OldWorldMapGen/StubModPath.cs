using System;
using System.Collections.Generic;
using TenCrowns.GameCore;

namespace OldWorldMapGen
{
    public class StubModPath : IModPath
    {
        private List<ModRecord> mods = new List<ModRecord>();

        public void InitMods(List<ModRecord> mods)
        {
            this.mods = mods ?? new List<ModRecord>();
        }

        public List<ModRecord> GetMods() => mods;
        public int GetNumMods() => mods.Count;
        public string GetVersionString() => "1.0.0";
        public string GetVersionAndModString() => "1.0.0";
        public string GetVersionAndModDisplayString() => "1.0.0";

        public bool TryLoadModsEnforceServerCRC(string versionAndMod, string gameID, List<string> modsNotFound) => false;
        public IModPath.LoadModError TryLoadMods(string versionAndMod, bool allowCompatible, bool checkCRC, List<string> modsNotFound) => IModPath.LoadModError.NONE;
        public string ParseVersion(string versionAndMod) => versionAndMod;
        public bool AddMod(string modname, List<string> unavailableMods = null) => false;
        public int SetMods(List<string> modnames, List<string> unavailableMods, bool showPopup) => 0;
        public void RemoveMod(string modname, bool checkScenarioDependencies = false, bool executeModChanged = true) { }
        public bool IsModLoaded(string modname) => false;
        public void ClearMods() { mods.Clear(); }
        public void SetStrictMode(bool strict) { }
        public bool IsStrictMode() => false;
        public List<ModRecord> GetIncompatibleMods() => new List<ModRecord>();
        public List<ModRecord> GetDependentMods() => new List<ModRecord>();
        public List<ModRecord> GetWhitelistMods() => new List<ModRecord>();
        public List<string> GetErrorList() => new List<string>();
        public string GetModDescription(string modName) => "";
        public bool IsCompatible(string versionAndModString, bool bStrict) => true;
        public void AddOnChangedListener(Action<List<ModRecord>, bool, bool> listener) { }
        public void RemoveOnChangedListener(Action<List<ModRecord>, bool, bool> listener) { }
        public void OnGameStarted() { }
        public void AddCRC(int crc) { }
        public int GetCRC() => 0;
    }
}
