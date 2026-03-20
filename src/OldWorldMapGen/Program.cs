using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using TenCrowns.GameCore;
using TenCrowns.GameCore.Text;
using TenCrowns.ClientCore;
using Mohawk.SystemCore;

namespace OldWorldMapGen
{
    class Program
    {
        private static string gameManagedDir;

        static int Main(string[] args)
        {
            var opts = ParseArgs(args);
            if (opts == null) return 1;

            if (opts.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            if (opts.ListMods)
            {
                var mods = ModLoader.ListAvailableMods();
                if (mods.Count == 0)
                {
                    Console.WriteLine("No mods found.");
                    string modsDir = ModLoader.GetModsDirectory();
                    Console.WriteLine($"Mods directory: {modsDir}");
                }
                else
                {
                    Console.WriteLine("Installed mods:");
                    foreach (var mod in mods)
                    {
                        string tags = string.IsNullOrEmpty(mod.Tags) ? "" : $" [{mod.Tags}]";
                        string author = string.IsNullOrEmpty(mod.Author) ? "" : $" by {mod.Author}";
                        Console.WriteLine($"  {mod.DisplayName,-30}{author}{tags}");
                    }
                }
                return 0;
            }

            // Resolve game directory
            string gameDir = ResolveGameDir(opts.GameDir);
            if (gameDir == null)
            {
                Console.Error.WriteLine("Error: Could not find Old World game directory.");
                Console.Error.WriteLine("Use --game-dir <path> to specify it.");
                return 1;
            }

            // Set up assembly resolution before touching any game types
            gameManagedDir = GetManagedPath(gameDir);
            if (!Directory.Exists(gameManagedDir))
            {
                Console.Error.WriteLine($"Error: Managed DLL directory not found: {gameManagedDir}");
                return 1;
            }
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            string xmlDir = Path.Combine(gameDir, "Reference", "XML", "Infos");
            if (!Directory.Exists(xmlDir))
            {
                Console.Error.WriteLine($"Error: XML directory not found: {xmlDir}");
                return 1;
            }

            // Patch Unity methods that require native runtime
            UnityPatches.Apply();

            return Run(opts, gameDir, xmlDir);
        }

        static int Run(Options opts, string gameDir, string xmlDir)
        {
            // Pre-load game assemblies so Infos reflection scan finds map script types
            foreach (string dllName in new[] { "TenCrowns.GameCore.dll", "Mohawk.SystemCore.dll" })
            {
                string dllPath = Path.Combine(gameManagedDir, dllName);
                if (File.Exists(dllPath))
                    Assembly.LoadFrom(dllPath);
            }

            // Load mod DLLs (must happen after game assemblies, before Infos init)
            var loadedMods = new List<ModLoader.ModInfo>();
            if (opts.Mods.Count > 0)
                loadedMods = ModLoader.LoadMods(opts.Mods);

            // Patch game methods that call Unity native functions (e.g., Mathf.PerlinNoise)
            UnityPatches.ApplyGamePatches();

            // Initialize game systems
            var xmlLoader = new FileSystemXMLLoader(xmlDir);
            if (loadedMods.Count > 0)
            {
                xmlLoader.AddModInfosDirs(loadedMods
                    .Where(m => m.InfosDir != null)
                    .Select(m => m.InfosDir).ToList());
            }

            ModSettings modSettings;
            try
            {
                modSettings = new ModSettings(new NullApplication(), xmlLoader,
                    new StubUserScriptManager(), new StubModPath(), null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error initializing game data: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }

            var infos = modSettings.Infos;
            Console.Error.WriteLine($"Loaded {(int)infos.mapClassesNum()} map scripts.");

            // Build script name lookup
            var scriptLookup = BuildScriptLookup(infos, modSettings.TextManager);

            // Handle --list-scripts
            if (opts.ListScripts)
            {
                PrintScriptList(scriptLookup);
                return 0;
            }

            // Handle --list-options
            if (opts.ListOptionsScript != null)
            {
                return ListOptions(opts.ListOptionsScript, scriptLookup, infos, modSettings.TextManager);
            }

            // Validate required args
            if (opts.ScriptName == null)
            {
                Console.Error.WriteLine("Error: --script is required.");
                PrintUsage();
                return 1;
            }
            if (opts.SizeName == null)
            {
                Console.Error.WriteLine("Error: --size is required.");
                return 1;
            }
            if (opts.NumPlayers < 0)
            {
                Console.Error.WriteLine("Error: --players is required.");
                return 1;
            }

            // Resolve script
            string normalizedScript = Normalize(opts.ScriptName);
            if (!scriptLookup.TryGetValue(normalizedScript, out var scriptEntry))
            {
                Console.Error.WriteLine($"Error: Unknown map script '{opts.ScriptName}'.");
                Console.Error.WriteLine("Available scripts:");
                PrintScriptList(scriptLookup);
                return 1;
            }

            // Resolve map size
            string sizeTypeStr = "MAPSIZE_" + opts.SizeName.ToUpper();
            var mapSizeType = infos.getType<MapSizeType>(sizeTypeStr);
            if ((int)mapSizeType < 0)
            {
                Console.Error.WriteLine($"Error: Unknown map size '{opts.SizeName}'. Use: smallest, tiny, small, medium, large, huge.");
                return 1;
            }

            // Resolve map class type for this script
            MapClassType mapClassType = MapClassType.NONE;
            for (MapClassType i = (MapClassType)0; (int)i < (int)infos.mapClassesNum(); i++)
            {
                if (infos.mapClass(i).mzScriptType == scriptEntry.ClassFullName)
                {
                    mapClassType = i;
                    break;
                }
            }

            // Find script Type via reflection
            string className = scriptEntry.ClassName;
            Type scriptType = FindScriptType(className);
            if (scriptType == null)
            {
                Console.Error.WriteLine($"Error: Could not find script class '{className}' in loaded assemblies.");
                return 1;
            }

            var ctorTypes = new[] { typeof(MapParameters).MakeByRefType(), typeof(Infos) };
            var ctor = scriptType.GetConstructor(ctorTypes);
            if (ctor == null)
            {
                Console.Error.WriteLine($"Error: Script class '{className}' does not have the expected constructor.");
                return 1;
            }

            // Set up output directory
            string outputDir = opts.OutputDir ?? "maps";
            Directory.CreateDirectory(outputDir);

            int maxAttempts = infos.Globals.MAP_BUILD_MAX_ATTEMPTS;
            if (maxAttempts <= 0) maxAttempts = 5;

            // Generate maps
            for (int mapIndex = 0; mapIndex < opts.Count; mapIndex++)
            {
                // Set up GameParameters
                var gameParams = new GameParameters();

                // Seed
                ulong seed;
                if (opts.Seed.HasValue)
                    seed = (ulong)(opts.Seed.Value + mapIndex);
                else
                {
                    int maxSeed = infos.Globals.MAX_SEED;
                    if (maxSeed <= 0) maxSeed = 100000000; // fallback matching globalsInt.xml
                    seed = (ulong)(DateTime.Now.Ticks % maxSeed);
                    if (seed == 0) seed = 1;
                }
                gameParams.ulFirstSeed = seed;
                gameParams.ulMapSeed = seed;

                // Map size and class
                gameParams.eMapSize = mapSizeType;
                gameParams.eMapClass = mapClassType;

                // Players
                int numPlayers = opts.NumPlayers;
                for (int i = 0; i < numPlayers; i++)
                {
                    var pp = new PlayerParameters();
                    pp.ID = i;
                    pp.Team = (TeamType)i;
                    gameParams.lPlayerParameters.Add(pp);
                }

                // For mirror: assign 2 teams
                if (opts.Mirror)
                {
                    for (int i = 0; i < numPlayers; i++)
                        gameParams.lPlayerParameters[i].Team = (TeamType)(i % 2);
                }

                // Wire toggle options
                WireSingleOption(gameParams, infos, opts.Mirror, "MAP_OPTIONS_SINGLE_MIRROR");
                WireSingleOption(gameParams, infos, opts.PointSymmetry, "MAP_OPTIONS_SINGLE_POINT_SYMMETRY");
                WireSingleOption(gameParams, infos, opts.ConnectedStarts, "MAP_OPTIONS_SINGLE_CONNECTED_STARTS");
                WireSingleOption(gameParams, infos, opts.FairStarts, "MAP_OPTIONS_SINGLE_FAIR_STARTS");
                WireSingleOption(gameParams, infos, opts.KingOfTheHill, "MAP_OPTIONS_SINGLE_KING_OF_THE_HILL");
                WireSingleOption(gameParams, infos, opts.GoodStartResources, "MAP_OPTIONS_SINGLE_GOOD_PLAYER_START_RESOURCES");
                WireSingleOption(gameParams, infos, opts.ForceRandomTribes, "MAP_OPTIONS_SINGLE_FORCE_RANDOM_TRIBES");
                WireSingleOption(gameParams, infos, opts.ExtraMountains, "MAP_OPTIONS_SINGLE_EXTRA_MOUNTAINS");
                WireSingleOption(gameParams, infos, opts.ExtraWater, "MAP_OPTIONS_SINGLE_EXTRA_WATER");

                // Wire multi-choice options
                WireMultiOption(gameParams, infos, opts.Resources, "MAP_OPTIONS_MULTI_RESOURCE_DENSITY", new Dictionary<string, string> {
                    {"high", "MAP_OPTION_HIGH_RESOURCES"}, {"medium", "MAP_OPTION_MEDIUM_RESOURCES"},
                    {"low", "MAP_OPTION_LOW_RESOURCES"}, {"random", "MAP_OPTION_RANDOM_RESOURCES"}
                });
                WireMultiOption(gameParams, infos, opts.CityDensity, "MAP_OPTIONS_CITY_SITE_DENSITY", new Dictionary<string, string> {
                    {"high", "MAP_OPTION_CITY_SITE_DENSITY_HIGH"}, {"medium", "MAP_OPTION_CITY_SITE_DENSITY_MEDIUM"},
                    {"low", "MAP_OPTION_CITY_SITE_DENSITY_LOW"}, {"random", "MAP_OPTION_CITY_SITE_DENSITY_RANDOM"}
                });
                WireMultiOption(gameParams, infos, opts.CityNumber, "MAP_OPTIONS_CITY_SITE_NUMBER", new Dictionary<string, string> {
                    {"high", "MAP_OPTION_CITY_SITE_NUMBER_HIGH"}, {"medium", "MAP_OPTION_CITY_SITE_NUMBER_MEDIUM"},
                    {"low", "MAP_OPTION_CITY_SITE_NUMBER_LOW"}, {"single", "MAP_OPTION_CITY_SITE_NUMBER_SINGLE"},
                    {"random", "MAP_OPTION_CITY_SITE_NUMBER_RANDOM"}
                });

                // Wire script-specific options via --map-option
                foreach (var kvp in opts.MapOptions)
                {
                    if (!WireRawOption(gameParams, infos, kvp.Key, kvp.Value))
                    {
                        Console.Error.WriteLine($"Warning: Could not resolve map option {kvp.Key}={kvp.Value}");
                    }
                }

                // Build map with retries
                var mapParams = new MapParameters(gameParams);
                IMapScriptInterface mapScript = null;
                bool success = false;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    if (attempt > 0)
                    {
                        gameParams.ulMapSeed = seed + (ulong)attempt;
                        gameParams.ulFirstSeed = gameParams.ulMapSeed;
                        mapParams = new MapParameters(gameParams);
                    }

                    try
                    {
                        var ctorArgs = new object[] { mapParams, infos };
                        mapScript = (IMapScriptInterface)ctor.Invoke(ctorArgs);
                        mapParams = (MapParameters)ctorArgs[0];

                        if (mapScript.Build())
                        {
                            success = true;
                            break;
                        }
                        Console.Error.WriteLine($"  Build attempt {attempt + 1}/{maxAttempts} failed: {mapScript.GetError()}");
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException ?? ex;
                        Console.Error.WriteLine($"  Build attempt {attempt + 1}/{maxAttempts} threw: {inner.Message}");
                    }
                }

                if (!success)
                {
                    Console.Error.WriteLine($"Error: Map generation failed after {maxAttempts} attempts.");
                    return 1;
                }

                // Write output
                string displayName = scriptEntry.DisplayName.Replace(' ', '_');
                string filename = $"{displayName}_{opts.SizeName}_{gameParams.ulMapSeed}.xml";
                string outputPath = Path.Combine(outputDir, filename);

                MapWriter.Write(outputPath, mapScript, infos);

                Console.WriteLine($"Map written: {outputPath}");
                Console.WriteLine($"  Script: {scriptEntry.DisplayName}  Size: {mapScript.MapWidth}x{mapScript.MapHeight}  Seed: {gameParams.ulMapSeed}  Tiles: {mapScript.GetTileData().Count}");
            }

            return 0;
        }

        // --- Script name resolution ---

        class ScriptInfo
        {
            public string DisplayName;
            public string ClassName;      // short name, e.g. "MapScriptContinent"
            public string ClassFullName;  // full name from mzScriptType
            public InfoMapClass Info;
        }

        static Dictionary<string, ScriptInfo> BuildScriptLookup(Infos infos, TextManager textManager)
        {
            var lookup = new Dictionary<string, ScriptInfo>();

            for (int i = 0; i < (int)infos.mapClassesNum(); i++)
            {
                var mc = infos.mapClass((MapClassType)i);
                if (mc == null) continue;

                // Skip RANDOM entry and hidden scripts
                if (mc.mbRandom || mc.mbHidden) continue;
                if (string.IsNullOrEmpty(mc.mzScriptType)) continue;

                string fullClassName = mc.mzScriptType;
                string shortClassName = fullClassName;
                if (shortClassName.Contains("."))
                    shortClassName = shortClassName.Substring(shortClassName.LastIndexOf('.') + 1);

                // Get display name from TextManager
                string displayName = null;
                try
                {
                    if (mc.mName != TextType.NONE && textManager != null)
                        displayName = textManager.TEXT(mc.mName);
                }
                catch { }

                if (string.IsNullOrEmpty(displayName))
                    displayName = mc.mzName; // fallback to TEXT_ key

                if (string.IsNullOrEmpty(displayName))
                    displayName = shortClassName;

                var info = new ScriptInfo
                {
                    DisplayName = displayName,
                    ClassName = shortClassName,
                    ClassFullName = fullClassName,
                    Info = mc
                };

                // Index by normalized display name
                lookup[Normalize(displayName)] = info;
                // Also index by class name variants
                lookup[Normalize(shortClassName)] = info;
                // Strip "MapScript" prefix for convenience
                if (shortClassName.StartsWith("MapScript"))
                    lookup[Normalize(shortClassName.Substring("MapScript".Length))] = info;
            }

            return lookup;
        }

        static string Normalize(string name)
        {
            if (name == null) return "";
            return new string(name.ToLowerInvariant()
                .Where(c => c != ' ' && c != '-' && c != '_')
                .ToArray());
        }

        static void PrintScriptList(Dictionary<string, ScriptInfo> lookup)
        {
            var seen = new HashSet<string>();
            var scripts = new List<ScriptInfo>();
            foreach (var kvp in lookup)
            {
                if (seen.Add(kvp.Value.ClassFullName))
                    scripts.Add(kvp.Value);
            }

            scripts.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine("Available map scripts:");
            foreach (var s in scripts)
            {
                bool allowsMirror = s.Info.mbAllowMirror;
                string mirrorTag = allowsMirror ? " [mirror]" : "";
                Console.WriteLine($"  {s.DisplayName,-25} ({s.ClassName}){mirrorTag}");
            }
        }

        static int ListOptions(string scriptName, Dictionary<string, ScriptInfo> lookup, Infos infos, TextManager textManager)
        {
            string normalized = Normalize(scriptName);
            if (!lookup.TryGetValue(normalized, out var entry))
            {
                Console.Error.WriteLine($"Error: Unknown map script '{scriptName}'.");
                return 1;
            }

            Console.WriteLine($"Options for {entry.DisplayName} ({entry.ClassName}):");
            Console.WriteLine();

            // Multi options
            if (entry.Info.maeCustomOptionsMulti.Count > 0)
            {
                Console.WriteLine("Multi-choice options (--map-option KEY=VALUE):");
                foreach (var multiType in entry.Info.maeCustomOptionsMulti)
                {
                    var multiInfo = infos.mapOptionsMulti(multiType);
                    string optName = multiInfo.mzType;
                    string displayName = optName;
                    try
                    {
                        if (multiInfo.mName != TextType.NONE && textManager != null)
                        {
                            string text = textManager.TEXT(multiInfo.mName);
                            if (!string.IsNullOrEmpty(text))
                                displayName = text;
                        }
                    }
                    catch { }

                    string defaultChoice = infos.mapOption(multiInfo.meDefault)?.mzType ?? "?";
                    Console.WriteLine($"  {displayName} ({optName})  [default: {defaultChoice}]");

                    foreach (var choice in multiInfo.maeChoices)
                    {
                        var choiceInfo = infos.mapOption(choice);
                        string choiceName = choiceInfo.mzType;
                        string choiceDisplay = choiceName;
                        try
                        {
                            if (choiceInfo.mName != TextType.NONE && textManager != null)
                            {
                                string text = textManager.TEXT(choiceInfo.mName);
                                if (!string.IsNullOrEmpty(text))
                                    choiceDisplay = text;
                            }
                        }
                        catch { }
                        Console.WriteLine($"    {choiceDisplay} ({choiceName})");
                    }
                    Console.WriteLine();
                }
            }

            // Single options
            if (entry.Info.maeCustomOptionsSingle.Count > 0)
            {
                Console.WriteLine("Toggle options (--map-option KEY=1 to enable):");
                foreach (var singleType in entry.Info.maeCustomOptionsSingle)
                {
                    var singleInfo = infos.mapOptionsSingle(singleType);
                    string optName = singleInfo.mzType;
                    string displayName = optName;
                    try
                    {
                        if (singleInfo.mName != TextType.NONE && textManager != null)
                        {
                            string text = textManager.TEXT(singleInfo.mName);
                            if (!string.IsNullOrEmpty(text))
                                displayName = text;
                        }
                    }
                    catch { }

                    bool defaultVal = singleInfo.mbDefault;
                    string validity = "";
                    if (!singleInfo.mbSinglePlayerValid) validity = " (MP only)";
                    if (!singleInfo.mbMultiPlayerValid) validity = " (SP only)";
                    Console.WriteLine($"  {displayName} ({optName})  [default: {(defaultVal ? "on" : "off")}]{validity}");
                }
            }

            return 0;
        }

        // --- Option wiring ---

        static void WireSingleOption(GameParameters gp, Infos infos, bool enabled, string typeStr)
        {
            if (!enabled) return;
            var optType = infos.getType<MapOptionsSingleType>(typeStr);
            if ((int)optType >= 0)
                gp.mapMapSingleOptions[optType] = 1;
        }

        static void WireMultiOption(GameParameters gp, Infos infos, string value, string multiTypeStr, Dictionary<string, string> valueMap)
        {
            if (value == null) return;
            string valueLower = value.ToLowerInvariant();
            if (!valueMap.TryGetValue(valueLower, out string optionTypeStr))
            {
                Console.Error.WriteLine($"Warning: Unknown value '{value}' for {multiTypeStr}. Valid: {string.Join(", ", valueMap.Keys)}");
                return;
            }
            var multiType = infos.getType<MapOptionsMultiType>(multiTypeStr);
            var optionValue = infos.getType<MapOptionType>(optionTypeStr);
            if ((int)multiType >= 0 && (int)optionValue >= 0)
                gp.mapMapMultiOptions[multiType] = optionValue;
        }

        static bool WireRawOption(GameParameters gp, Infos infos, string key, string value)
        {
            // Try as multi option
            var multiType = infos.getType<MapOptionsMultiType>(key);
            if ((int)multiType >= 0)
            {
                var optionValue = infos.getType<MapOptionType>(value);
                if ((int)optionValue >= 0)
                {
                    gp.mapMapMultiOptions[multiType] = optionValue;
                    return true;
                }
            }

            // Try as single option
            var singleType = infos.getType<MapOptionsSingleType>(key);
            if ((int)singleType >= 0)
            {
                if (int.TryParse(value, out int intVal))
                {
                    gp.mapMapSingleOptions[singleType] = intVal;
                    return true;
                }
            }

            return false;
        }

        // --- Script type resolution ---

        static Type FindScriptType(string className)
        {
            Type mapScriptInterfaceType = typeof(IMapScriptInterface);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == className &&
                            mapScriptInterfaceType.IsAssignableFrom(type) &&
                            !type.IsAbstract)
                        {
                            return type;
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }
            return null;
        }

        // --- Assembly resolution ---

        static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            string path = Path.Combine(gameManagedDir, name);
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
            return null;
        }

        // --- Game directory resolution ---

        static string ResolveGameDir(string userSpecified)
        {
            if (userSpecified != null)
            {
                if (Directory.Exists(userSpecified))
                    return userSpecified;
                Console.Error.WriteLine($"Warning: Specified game directory not found: {userSpecified}");
            }

            // Auto-detect per platform
            string[] candidates;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidates = new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library/Application Support/Steam/steamapps/common/Old World")
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                candidates = new[] {
                    @"C:\Program Files (x86)\Steam\steamapps\common\Old World",
                    @"C:\Program Files\Steam\steamapps\common\Old World"
                };
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                candidates = new[] {
                    Path.Combine(home, ".steam/steam/steamapps/common/Old World"),
                    Path.Combine(home, ".local/share/Steam/steamapps/common/Old World")
                };
            }

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        static string GetManagedPath(string gameDir)
        {
            // macOS
            string macPath = Path.Combine(gameDir, "OldWorld.app", "Contents", "Resources", "Data", "Managed");
            if (Directory.Exists(macPath))
                return macPath;

            // Windows/Linux
            string winPath = Path.Combine(gameDir, "OldWorld_Data", "Managed");
            if (Directory.Exists(winPath))
                return winPath;

            // Fallback — try macOS path anyway
            return macPath;
        }

        // --- CLI argument parsing ---

        class Options
        {
            public string ScriptName;
            public string SizeName;
            public int NumPlayers = -1;
            public string GameDir;
            public string OutputDir;
            public int Count = 1;
            public long? Seed;
            public bool ListScripts;
            public string ListOptionsScript;
            public bool ShowHelp;

            // Toggle options
            public bool Mirror;
            public bool PointSymmetry;
            public bool ConnectedStarts;
            public bool FairStarts;
            public bool KingOfTheHill;
            public bool GoodStartResources;
            public bool ForceRandomTribes;
            public bool ExtraMountains;
            public bool ExtraWater;

            // Multi-choice options
            public string Resources;
            public string CityDensity;
            public string CityNumber;

            // Raw map options
            public List<KeyValuePair<string, string>> MapOptions = new List<KeyValuePair<string, string>>();

            // Mods
            public List<string> Mods = new List<string>();
            public bool ListMods;
        }

        static Options ParseArgs(string[] args)
        {
            var opts = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--help": case "-h":
                        opts.ShowHelp = true;
                        return opts;
                    case "--list-scripts":
                        opts.ListScripts = true;
                        break;
                    case "--list-options":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --list-options requires a script name."); return null; }
                        opts.ListOptionsScript = args[i];
                        break;
                    case "--script":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --script requires a value."); return null; }
                        opts.ScriptName = args[i];
                        break;
                    case "--size":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --size requires a value."); return null; }
                        opts.SizeName = args[i].ToLowerInvariant();
                        break;
                    case "--players":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --players requires a value."); return null; }
                        if (!int.TryParse(args[i], out int p) || p < 2 || p > 7)
                        { Console.Error.WriteLine("Error: --players must be 2-7."); return null; }
                        opts.NumPlayers = p;
                        break;
                    case "--game-dir":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --game-dir requires a value."); return null; }
                        opts.GameDir = args[i];
                        break;
                    case "--output":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --output requires a value."); return null; }
                        opts.OutputDir = args[i];
                        break;
                    case "--count":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --count requires a value."); return null; }
                        if (!int.TryParse(args[i], out int c) || c < 1)
                        { Console.Error.WriteLine("Error: --count must be >= 1."); return null; }
                        opts.Count = c;
                        break;
                    case "--seed":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --seed requires a value."); return null; }
                        if (!long.TryParse(args[i], out long s) || s < 1 || s > 99999999)
                        { Console.Error.WriteLine("Error: --seed must be 1-99999999."); return null; }
                        opts.Seed = s;
                        break;

                    // Toggle options
                    case "--mirror": opts.Mirror = true; break;
                    case "--point-symmetry": opts.PointSymmetry = true; break;
                    case "--connected-starts": opts.ConnectedStarts = true; break;
                    case "--fair-starts": opts.FairStarts = true; break;
                    case "--king-of-the-hill": opts.KingOfTheHill = true; break;
                    case "--good-start-resources": opts.GoodStartResources = true; break;
                    case "--force-random-tribes": opts.ForceRandomTribes = true; break;
                    case "--extra-mountains": opts.ExtraMountains = true; break;
                    case "--extra-water": opts.ExtraWater = true; break;

                    // Multi-choice options
                    case "--resources":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --resources requires a value."); return null; }
                        opts.Resources = args[i];
                        break;
                    case "--city-density":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --city-density requires a value."); return null; }
                        opts.CityDensity = args[i];
                        break;
                    case "--city-number":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --city-number requires a value."); return null; }
                        opts.CityNumber = args[i];
                        break;

                    // Mods
                    case "--mod":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --mod requires a name or path."); return null; }
                        opts.Mods.Add(args[i]);
                        break;
                    case "--list-mods":
                        opts.ListMods = true;
                        break;

                    // Script-specific options
                    case "--map-option":
                        if (++i >= args.Length) { Console.Error.WriteLine("Error: --map-option requires KEY=VALUE."); return null; }
                        int eq = args[i].IndexOf('=');
                        if (eq < 0) { Console.Error.WriteLine($"Error: --map-option value must be KEY=VALUE, got '{args[i]}'."); return null; }
                        opts.MapOptions.Add(new KeyValuePair<string, string>(
                            args[i].Substring(0, eq), args[i].Substring(eq + 1)));
                        break;

                    default:
                        Console.Error.WriteLine($"Error: Unknown argument '{arg}'.");
                        return null;
                }
            }

            return opts;
        }

        static void PrintUsage()
        {
            Console.WriteLine(@"OldWorldMapGen — Old World random map generator

Usage: OldWorldMapGen [options]

Required:
  --script <name>           Map script (e.g., Continent, ""Inland Sea"", Donut)
  --size <size>             smallest|tiny|small|medium|large|huge
  --players <n>             Number of players (2-7)

Optional:
  --game-dir <path>         Game install path (auto-detected if not specified)
  --output <path>           Output directory (default: ./maps/)
  --count <n>               Number of maps to generate (default: 1)
  --mod <name|path>         Load a mod (repeatable, e.g., --mod ""Middle Kingdom"")
  --list-scripts            List available map scripts and exit
  --list-options <script>   List options for a script and exit
  --list-mods               List installed mods and exit

Advanced:
  --seed <n>                Map seed 1-99999999 (default: random)

Toggle options:
  --mirror                  Mirror map (MP only)
  --point-symmetry          Point symmetry (MP only)
  --connected-starts        Connected starting positions (MP only)
  --fair-starts             Fair starting positions
  --king-of-the-hill        King of the hill
  --good-start-resources    Good resources at start
  --force-random-tribes     Randomize tribe placement
  --extra-mountains         Extra mountain ranges
  --extra-water             Extra water features

Multi-choice options:
  --resources <v>           high|medium|low|random (default: medium)
  --city-density <v>        high|medium|low|random
  --city-number <v>         high|medium|low|single|random

Script-specific options:
  --map-option KEY=VALUE    Set script-specific option (repeatable)
                            Use --list-options <script> to see available options

Examples:
  OldWorldMapGen --script Continent --size medium --players 5
  OldWorldMapGen --script Donut --size large --players 4 --mirror
  OldWorldMapGen --script Archipelago --size huge --count 5 --resources high
  OldWorldMapGen --list-options ""Inland Sea""");
        }
    }
}
