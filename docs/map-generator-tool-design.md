# Old World Map Generator Tool — Design Document

A standalone C# console app that generates random Old World maps by calling the game's actual map generation code. Instead of reimplementing the algorithms, this tool references the game's managed DLLs directly and provides a thin shim layer to run them outside Unity.

## Architecture

```
┌──────────────────────────────────────────────────────┐
│  OldWorldMapGen (Console App)                        │
│                                                      │
│  ┌─────────────────┐  ┌──────────────────────────┐   │
│  │ CLI / Main       │  │ Custom Stubs (3 classes) │   │
│  │ - parse args     │  │ - FileSystemXMLLoader    │   │
│  │ - setup Infos    │  │ - StubModPath            │   │
│  │ - run map script │  │ - StubUserScriptManager  │   │
│  │ - write XML      │  │                          │   │
│  └────────┬─────────┘  └──────────┬───────────────┘   │
│           │                       │                   │
│           ▼                       ▼                   │
│  ┌────────────────────────────────────────────────┐   │
│  │ Game DLLs (referenced, not modified)           │   │
│  │                                                │   │
│  │ TenCrowns.GameCore.dll                         │   │
│  │   DefaultMapScript, MapScriptContinent, ...    │   │
│  │   Infos, ModSettings, GameFactory              │   │
│  │   MapBuilder, TileData, MapData                │   │
│  │   NullApplication (reused directly)            │   │
│  │                                                │   │
│  │ Mohawk.SystemCore.dll                          │   │
│  │   Fractal, NoiseGenerator, RandomStruct        │   │
│  │                                                │   │
│  │ Assembly-CSharp.dll                            │   │
│  │   (only if needed for types not in GameCore)   │   │
│  └────────────────────────────────────────────────┘   │
│                                                      │
│  Reads XML from: <game-dir>/Reference/XML/Infos/     │
│  Writes maps to: <output-dir>/*.xml                  │
└──────────────────────────────────────────────────────┘
```

**DLL locations (macOS):**
```
OldWorld.app/Contents/Resources/Data/Managed/
  TenCrowns.GameCore.dll
  Mohawk.SystemCore.dll
  Assembly-CSharp.dll
  UnityEngine.CoreModule.dll   (needed for Vector3, etc.)
  UnityEngine.dll              (facade)
```

The game DLLs target .NET Framework 4.x (Mono). The console app should target `net472` or `net48` to match, or use `netstandard2.0` compatibility.

## Initialization Chain

The game's `Infos` database must be loaded before any map script can run. The normal game initialization flows through several objects that assume a Unity runtime. We replace the Unity-dependent pieces with lightweight stubs.

### Normal game flow (what we're replacing)

```
AppMain → ModPath(Unity Resources) → InfosXMLLoader(ModPath)
       → UserScriptManager → sets Factory
       → ModSettings(app, xmlLoader, userScriptMgr, modPath, null)
         → Init() → Infos(modSettings) → LoadInfo() → parses all XML
```

### Standalone flow

```
StubModPath()
FileSystemXMLLoader(xmlDir)
StubUserScriptManager()        ← sets Factory = new GameFactory()
NullApplication()              ← from game DLL, already headless

ModSettings(nullApp, xmlLoader, stubScriptMgr, stubModPath, null)
  └→ Init()
       └→ stubScriptMgr.Initialize(modSettings)  ← sets Factory
       └→ Infos = Factory.CreateInfos(this)
            └→ Infos(modSettings) constructor
                 └→ BuildListOfInfoFiles()        ← registers 160+ XML files
                 └→ LoadInfo()
                      └→ ResetCache()
                      └→ ReadRemovedInfoListTypes()
                      └→ ReadInfoListTypes()      ← calls GetDefaultXML for each
                      └→ ReadInfoListData()       ← calls GetDefaultXML + GetModdedXML
```

After this, `modSettings.Infos` is fully populated and ready for map scripts.

### Constructor signatures (from decompiled source)

```csharp
// ModSettings — the central wiring point
public ModSettings(
    IApplication app,
    IInfosXMLLoader xmlLoader,
    UserScriptManagerBase userScriptManager,
    IModPath modPath,
    Infos infos    // pass null → Factory creates it
)

// Infos — created by Factory inside ModSettings.Init()
public Infos(ModSettings modSettings)
// Constructor calls BuildListOfInfoFiles() then LoadInfo()

// GameFactory — creates Infos and helpers
public class GameFactory {
    public virtual Infos CreateInfos(ModSettings pModSettings) => new Infos(pModSettings);
    public virtual InfoGlobals CreateInfoGlobals() => new InfoGlobals();
    public virtual InfoHelpers CreateInfoHelpers(Infos pInfos) => new InfoHelpers(pInfos);
    public virtual TextManager CreateTextManager(Infos pInfos, LanguageType currentLanguage) => ...;
    public virtual ColorManager CreateColorManager(Infos pInfos, TextManager textManager) => ...;
}
```

## Custom Stubs

### 1. FileSystemXMLLoader : IInfosXMLLoader

The core piece we implement. Reads XML from `Reference/XML/Infos/` on disk instead of Unity asset bundles.

**Interface (10 methods):**

```csharp
public interface IInfosXMLLoader
{
    void ResetCache(bool resetDefaultXML);
    bool IsValidateOverride();
    IInfoXmlFieldDataProvider GetFieldDataProvider();
    List<XmlDocument> GetDefaultXML(string resourceName);
    List<XmlDocument> GetDefaultXML(string resourceName, out List<string> xmlPaths);
    List<XmlDocument> GetChangedXML(string resourceName);
    List<XmlDocument> GetChangedXML(string resourceName, out List<string> xmlPaths);
    List<XmlDocument> GetModdedXML(string resourceName, ModdedXMLType searchType);
    List<XmlDocument> GetModdedXML(string resourceName, ModdedXMLType searchType, out List<string> xmlPaths);
    XmlDocument GetMergedXmlForAsset(XmlDocument baseDoc, List<string> moddedAssets);
}
```

**Implementation by method:**

| Method | Implementation |
|--------|---------------|
| `ResetCache` | No-op (we don't cache) |
| `IsValidateOverride` | Return `false` |
| `GetFieldDataProvider` | Return `null` (game's impl does too) |
| `GetDefaultXML(name)` | **The real work** — see below |
| `GetDefaultXML(name, out paths)` | Same + populate paths list |
| `GetChangedXML` | Return empty list (only 1 `-change` file exists in base game) |
| `GetModdedXML` | Return empty list (no mods in standalone mode) |
| `GetMergedXmlForAsset` | Return `null` (no fragment merging) |

**GetDefaultXML logic:**

When `Infos` requests `"Infos/terrain"`:

1. Extract the base filename: `"terrain"` (strip the `"Infos/"` prefix)
2. Scan the XML directory for all files matching this base name
3. Apply the game's filename matching rules:
   - `terrain.xml` → exact match → include
   - `terrain-btt.xml` → suffix `-btt` (not `-change` or `-append`) → include as ADD file
   - `terrain-wd.xml` → same → include as ADD file
4. Parse each matching file as `XmlDocument`
5. Return `List<XmlDocument>` (base file first, then add files)

**Filename matching rules** (from `ModPath.IsMatchingFilename`, lines 2304-2340):

```
Given base "bonus-event" and candidate filename:
  "bonus-event"        → EXACT match
  "bonus-event-btt"    → ADD match (suffix is not -change or -append)
  "bonus-event-wog"    → ADD match
  "bonus-event-change" → CHANGE match (only if CHANGE flag requested)
  "bonus-event-append" → APPEND match (only if APPEND flag requested)
  "bonus"              → NO match (doesn't start with full base + hyphen)
```

The `GetDefaultXML` call uses flags `EXACT | ADD`, so it returns the base file plus all DLC add files. This is how `bonus-event-btt.xml`, `mapOption-wog.xml`, etc. get loaded alongside their base files.

**XML format on disk** (matches what game expects via `SelectNodes("Root/Entry")`):

```xml
<?xml version="1.0"?>
<Root>
  <Entry>
    <zType/>          <!-- template row, ignored -->
    <Name/>
    ...
  </Entry>
  <Entry>
    <zType>TERRAIN_WATER</zType>
    <Name>TEXT_TERRAIN_WATER</Name>
    ...
  </Entry>
  ...
</Root>
```

**File count:** 338 XML files in `Reference/XML/Infos/`. All 160+ info types registered by `BuildListOfInfoFiles()` are present.

### 2. StubModPath : IModPath

Minimal implementation of the 20+ method interface. Most methods are no-ops.

```csharp
public class StubModPath : IModPath
{
    private List<ModRecord> mods = new List<ModRecord>();

    public void InitMods(List<ModRecord> mods) { this.mods = mods ?? new List<ModRecord>(); }
    public List<ModRecord> GetMods() => mods;
    public int GetNumMods() => mods.Count;
    public string GetVersionString() => "1.0.0";
    public string GetVersionAndModString() => "1.0.0";
    public string GetVersionAndModDisplayString() => "1.0.0";

    // Everything else: no-ops or empty returns
    public bool TryLoadModsEnforceServerCRC(...) => false;
    public LoadModError TryLoadMods(...) => LoadModError.NONE;
    public string ParseVersion(string v) => v;
    public bool AddMod(...) => false;
    public int SetMods(...) => 0;
    public void RemoveMod(...) { }
    public bool IsModLoaded(string m) => false;
    public void ClearMods() { mods.Clear(); }
    public void SetStrictMode(bool s) { }
    public bool IsStrictMode() => false;
    public List<ModRecord> GetIncompatibleMods() => new();
    public List<ModRecord> GetDependentMods() => new();
    public List<ModRecord> GetWhitelistMods() => new();
    public List<string> GetErrorList() => new();
    public string GetModDescription(string n) => "";
    public bool IsCompatible(string v, bool s) => true;
    public void AddOnChangedListener(...) { }
    public void RemoveOnChangedListener(...) { }
    public void OnGameStarted() { }
    public void AddCRC(int crc) { }
    public int GetCRC() => 0;
}
```

### 3. StubUserScriptManager : UserScriptManagerBase

The game's `UserScriptManager` iterates through mods loading DLLs. At the end, if no mod provided a custom `GameFactory`, it sets `modSettings.Factory = new GameFactory()`. Our stub just does that immediately.

```csharp
public class StubUserScriptManager : UserScriptManagerBase
{
    public override void Initialize(ModSettings modSettings)
    {
        modSettings.Factory = new GameFactory();
    }

    // All other 16 abstract methods: empty no-ops
    public override void Shutdown() { }
    public override void OnClientUpdate() { }
    public override void OnClientPostUpdate() { }
    public override void OnServerUpdate() { }
    public override void OnNewTurnServer() { }
    public override bool CallOnGUI() => false;
    public override void OnGUI() { }
    public override void OnPreGameServer() { }
    public override void OnGameServerReady() { }
    public override void OnGameClientReady() { }
    public override void OnPreLoad() { }
    public override void OnPostLoad() { }
    public override void OnRendererReady() { }
    public override void OnGameOver() { }
    public override void OnExitGame() { }
}
```

## Map Script Invocation

### How the game does it

`MapBuilder.assignMapScript()` uses reflection to find and instantiate map scripts:

```csharp
// 1. Strip namespace if present ("TenCrowns.GameCore.MapScriptContinent" → "MapScriptContinent")
if (zScriptName.Contains("."))
    zScriptName = zScriptName.Split('.', 2)[1];

// 2. Search all loaded assemblies for matching class name
Type mapScript = GetMapScript(zScriptName);
// GetMapScript scans AppDomain.CurrentDomain.GetAssemblies() for:
//   type.Name == zScriptName && IMapScriptInterface.IsAssignableFrom(type) && !type.IsAbstract

// 3. Invoke constructor via reflection
Type[] types = { typeof(MapParameters).MakeByRefType(), typeof(Infos) };
object[] parameters = { pParams, pInfos };
mpMapScript = mapScript.GetConstructor(types)?.Invoke(parameters) as IMapScriptInterface;
```

All map script constructors have the same signature:
```csharp
public MapScriptContinent(ref MapParameters mapParameters, Infos infos)
    : base(ref mapParameters, infos) { ... }
```

### How we do it

We can reuse `MapBuilder` directly, or replicate its reflection pattern. Using `MapBuilder`:

```csharp
var builder = new MapBuilder();
var mapParams = new MapParameters(gameParams);
// mapParams.iWidth and iHeight are set by the script's SetMapSize() during Build()

bool assigned = builder.assignMapScript("MapScriptContinent", ref mapParams, infos);
// Then call Build() on the script (via builder or directly)
```

Or bypass `MapBuilder` and instantiate directly, which gives us more control over error handling and output.

### Available map scripts

The game ships 20 map script classes in `TenCrowns.GameCore.dll`:

| Class Name | Inherits From | Description |
|-----------|--------------|-------------|
| `MapScriptContinent` | DefaultMapScript | Single large landmass |
| `MapScriptContinents` | DefaultMapScript | Multiple landmasses |
| `MapScriptArchipelago` | DefaultMapScript | Many islands |
| `MapScriptDonut` | DefaultMapScript | Ring with inner/outer seas |
| `MapScriptInlandSea2` | DefaultMapScript | Land surrounding central water |
| `MapScriptSeaside` | DefaultMapScript | Coastal strip |
| `MapScriptBay` | MapScriptSeaside | Land with bay indentation |
| `MapScriptLakesAndGulfs` | MapScriptSeaside | Multiple water features |
| `MapScriptMediterranean` | MapScriptSeaside | Central sea between landmasses |
| `MapScriptDisjunction` | DefaultMapScript | Disconnected continents |
| `MapScriptTumblingMountain` | MapScriptDisjunction | Unstable mountain mechanic |
| `MapScriptHardwoodForest` | DefaultMapScript | Heavy forest coverage |
| `MapScriptNorthernOcean` | DefaultMapScript | Arctic/polar map |
| `MapScriptDesert` | DefaultMapScript | Arid-dominant |
| `MapScriptPlayerIslands` | DefaultMapScript | Each player on own island |
| `MapScriptRejuvenation` | DefaultMapScript | DLC special type |
| `CoastalRainBasin` | DefaultMapScript | Tropical/wet coastal |
| `MapScriptHighlands` | CoastalRainBasin | Elevated terrain |
| `MapScriptDesolation` | CoastalRainBasin | Extreme harsh terrain |
| `MapScriptEbbingSea` | MapScriptArchipelago | Receding water mechanic |

### Map script name source

In the normal game, the script name comes from `mapClass.xml` via the `zScriptType` field:
```xml
<Entry>
    <zType>MAPCLASS_CONTINENT</zType>
    <zScriptType>MapScriptContinent</zScriptType>
</Entry>
```

However, these entries are **only in the Unity asset bundles** — the `Reference/XML/Infos/mapClass.xml` on disk contains only `MAPCLASS_RANDOM`. Our tool must accept script class names directly as CLI arguments rather than looking them up through Infos.

## Map Generation Pipeline

`DefaultMapScript.Build()` executes 27 stages in order:

```
 1. InitMapData()              — parse options, create MapData, init noise generators
 2. InitTiles()                — allocate tile grid
 3. GenerateLand()             — fractal land/water mask (overridden per script)
 4. ChoosePrimaryWindDirection()
 5. GenerateDeserts()          — desert regions if enabled
 6. GenerateMountains()        — 2-8 chains at random angles
 7. EliminateSingletonMountains()
 8. GenerateElevations()       — hills/plateaus via Perlin noise
 9. GenerateRivers()           — route downhill from mountains
10. ConvertOverloadedRiverTilesToLakes()
11. FillLakes()
12. BuildWaterAreas()          — identify distinct water regions
13. ModifyTerrain()            — adjust terrain by elevation/climate
14. SmoothTerrain()
15. HandleRainEffects()        — rain shadow from wind + mountains
16. BuildVegetation()          — forests/scrub via Perlin noise
17. DoMirrorMap()              — symmetry if enabled
18. FinalTerrainCleanup()
19. SetBoundaryTiles()         — mark impassable edges
20. SetUnreachableAreas()
21. BuildContinents()          — label landmasses
22. AddCities()                — place city sites (can fail → retry)
23. AddResources()             — distribute by density/type/distance
24. AddMiddleRowCitiesToMirrorMap()
25. PlaceTribes()              — barbarian sites
26. AddBonusImprovements()     — monuments, ruins, etc.
27. AddMapElementNames()       — name mountains, rivers, seas
```

`Build()` returns `false` if city placement fails (not enough valid locations). The game retries up to `MAP_BUILD_MAX_ATTEMPTS` times with different seeds. Our tool should do the same.

After `Build()`, three additional methods can be called separately:
- `BuildResources(MapData)` — additional resource placement
- `BuildBonusImprovements(MapData)` — additional bonus improvements
- `BuildElementNames(MapData)` — geographic feature names

These require a populated `MapData` object and are called by the game after the initial tile data is loaded. For standalone map generation, the resources and improvements placed during `Build()` (stages 23 and 26) should be sufficient for a playable map.

## GameParameters Setup

The map script reads its configuration from `MapParameters`, which wraps `GameParameters`:

```csharp
public struct MapParameters
{
    public GameParameters gameParams;
    public int iWidth;    // -1 initially, set by script's SetMapSize()
    public int iHeight;   // -1 initially, set by script's SetMapSize()
}

public class GameParameters
{
    public List<PlayerParameters> lPlayerParameters;  // one per player slot
    public ulong ulFirstSeed;
    public ulong ulMapSeed;           // seed for map generation
    public MapClassType eMapClass;    // enum, but may be NONE for standalone
    public MapSizeType eMapSize;      // MAPSIZE_SMALL, MAPSIZE_MEDIUM, etc.
    public SetList<GameOptionType> setGameOptions;
    public DictionaryList<MapOptionsMultiType, MapOptionType> mapMapMultiOptions;
    public DictionaryList<MapOptionsSingleType, int> mapMapSingleOptions;
    // ... other fields (scenario, difficulty, etc.)
}
```

**Minimum setup for map generation:**
- `ulMapSeed` — random seed (use `ulFirstSeed` as well)
- `eMapSize` — determines tile count (e.g., `MAPSIZE_MEDIUM` = 5,476 tiles)
- `lPlayerParameters` — at least 2 entries (determines player starts)
- Map options can be left at defaults

**Map sizes** (from `mapSize.xml`):

| MapSizeType | Tiles | Default Players |
|------------|-------|----------------|
| `MAPSIZE_SMALLEST` | 2,025 | 2 |
| `MAPSIZE_TINY` | 3,364 | 3 |
| `MAPSIZE_SMALL` | 4,356 | 4 |
| `MAPSIZE_MEDIUM` | 5,476 | 5 |
| `MAPSIZE_LARGE` | 6,724 | 6 |
| `MAPSIZE_HUGE` | 8,100 | 7 |

## XML Output Format

### Writing tiles

`TileData.writeXML()` serializes each tile using `XmlWriter`. The method signature:

```csharp
public virtual void writeXML(Infos pInfos, XmlWriter pWriter,
    List<DynastyType> aDynasties, OccurrenceType eOccurrence)
```

For standalone output, pass `null` for dynasties and `OccurrenceType.NONE`.

**Enum-to-string conversion** (important — not just `ToString()`):
- Terrain, Height, Vegetation, Resource, Improvement, TribeSite, NationSite: looked up via `pInfos.TYPE(enumValue).mzType` (returns the XML string like `"TERRAIN_WATER"`)
- CitySite: direct `enum.ToString()` (e.g., `"ACTIVE_START"`)
- Rivers: cast to `sbyte` numeric value

### Output structure

```xml
<?xml version="1.0" encoding="utf-8"?>
<Root
  MapWidth="50"
  MinLatitude="20"
  MaxLatitude="50"
  MapEdgesSafe="False"
  MinCitySiteDistance="4">
  <Tile
    ID="0">
    <Boundary />
    <Terrain>TERRAIN_SAND</Terrain>
    <Height>HEIGHT_FLAT</Height>
    <Metadata>IsAutoBoundary=true</Metadata>
  </Tile>
  <Tile
    ID="1">
    <Terrain>TERRAIN_TEMPERATE</Terrain>
    <Height>HEIGHT_HILL</Height>
    <Vegetation>VEGETATION_TREES</Vegetation>
    <Resource>RESOURCE_ORE</Resource>
  </Tile>
  <Tile
    ID="2">
    <Terrain>TERRAIN_URBAN</Terrain>
    <Height>HEIGHT_FLAT</Height>
    <CitySite>ACTIVE_START</CitySite>
    <Improvement>IMPROVEMENT_CITY_SITE</Improvement>
  </Tile>
  <!-- ... width*height tiles total ... -->
</Root>
```

Root attributes from the map script:
- `MapWidth` — `script.MapWidth` (required)
- `MinLatitude` — `script.MinLatitude` (controls climate bands)
- `MaxLatitude` — `script.MaxLatitude`
- `MapEdgesSafe` — `script.MapEdgesSafe` (east-west wrapping)
- `MinCitySiteDistance` — `script.MinCitySiteDistance`

### Writing output (our code)

```csharp
// After Build() succeeds:
List<TileData> tiles = mapScript.GetTileData();

using var writer = XmlWriter.Create(outputPath, new XmlWriterSettings {
    Indent = true,
    Encoding = Encoding.UTF8
});

writer.WriteStartElement("Root");
writer.WriteAttributeString("MapWidth", mapScript.MapWidth.ToString());
writer.WriteAttributeString("MinLatitude", mapScript.MinLatitude.ToString());
writer.WriteAttributeString("MaxLatitude", mapScript.MaxLatitude.ToString());
writer.WriteAttributeString("MapEdgesSafe", mapScript.MapEdgesSafe.ToString());
writer.WriteAttributeString("MinCitySiteDistance", mapScript.MinCitySiteDistance.ToString());

foreach (var tile in tiles)
{
    tile.writeXML(infos, writer, null, OccurrenceType.NONE);
}

writer.WriteEndElement();
```

## CLI Interface

```
OldWorldMapGen [options]

Required:
  --game-dir <path>       Path to Old World game directory
  --script <name>         Map script class name (e.g., MapScriptContinent)

Optional:
  --size <size>           Map size: smallest|tiny|small|medium|large|huge (default: medium)
  --players <n>           Number of player slots (default: size-appropriate)
  --seed <n>              Map seed, unsigned 64-bit (default: random)
  --count <n>             Number of maps to generate (default: 1)
  --output <path>         Output directory (default: ./maps/)
  --list-scripts          List all available map script names and exit

Examples:
  OldWorldMapGen --game-dir /path/to/OldWorld --script MapScriptContinent
  OldWorldMapGen --game-dir /path/to/OldWorld --script MapScriptDonut --size large --count 5
  OldWorldMapGen --game-dir /path/to/OldWorld --script MapScriptArchipelago --seed 42 --players 4
```

Output filenames: `<script>_<size>_<seed>.xml` (e.g., `MapScriptContinent_medium_12345.xml`)

When `--count` > 1, each map uses a different seed (sequential from base seed, or random if no seed specified).

## Project Structure

```
tools/mapgen/
  OldWorldMapGen.csproj          # .NET console app, references game DLLs
  Program.cs                     # CLI entry point, orchestration
  FileSystemXMLLoader.cs         # IInfosXMLLoader implementation
  StubModPath.cs                 # IModPath stub
  StubUserScriptManager.cs       # UserScriptManagerBase stub
  MapWriter.cs                   # XML output serialization
```

**Project file key points:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net472</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Game DLL references — paths resolved at build time via game-dir -->
    <Reference Include="TenCrowns.GameCore">
      <HintPath>$(GameDir)/OldWorld.app/Contents/Resources/Data/Managed/TenCrowns.GameCore.dll</HintPath>
    </Reference>
    <Reference Include="Mohawk.SystemCore">
      <HintPath>$(GameDir)/OldWorld.app/Contents/Resources/Data/Managed/Mohawk.SystemCore.dll</HintPath>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(GameDir)/OldWorld.app/Contents/Resources/Data/Managed/UnityEngine.CoreModule.dll</HintPath>
    </Reference>
    <Reference Include="UnityEngine">
      <HintPath>$(GameDir)/OldWorld.app/Contents/Resources/Data/Managed/UnityEngine.dll</HintPath>
    </Reference>
    <!-- Assembly-CSharp only if needed for specific types -->
  </ItemGroup>
</Project>
```

Build: `dotnet build tools/mapgen/ -p:GameDir="/path/to/Old World"`

## Risks and Open Questions

### Likely issues

1. **UnityEngine types at runtime.** The game DLLs reference `UnityEngine.Vector3`, `UnityEngine.Debug`, `UnityProfileScope`, etc. These will resolve at compile time via the referenced DLLs, but at runtime the Unity engine isn't initialized. If any code path calls `Debug.Log` or `Resources.Load`, it will throw. Our stubs prevent `Resources.Load` calls, but logging calls in the game code (e.g., `MohawkLog.Log`) may need attention. Likely fix: the Mono runtime will load the UnityEngine DLLs and the static methods will either work as no-ops or need a thin shim.

2. **TextManager initialization.** `ModSettings.Init()` creates a `TextManager` after `Infos`. If `TextManager` fails to initialize (e.g., missing text XML files), the whole chain could break. The `Reference/XML/Infos/` folder does contain text files (`text-*.xml`), so this should work, but it's a risk to test early.

3. **Missing XML files.** `BuildListOfInfoFiles()` registers 160+ file paths. If any expected file is missing from `Reference/XML/Infos/`, the loader returns an empty list and `Infos` may skip it silently or throw. Need to verify all registered paths have corresponding files on disk.

4. **Assembly-CSharp dependency.** Some types used by map scripts might be defined in `Assembly-CSharp.dll` rather than `TenCrowns.GameCore.dll`. If so, we need to reference that DLL too. `UserScriptManager` and `ModPath` are in Assembly-CSharp, but we're replacing those with stubs.

5. **MapClassType enum.** The `mapClass.xml` in `Reference/XML/` only defines `MAPCLASS_RANDOM`. The actual map class entries (Continent, Donut, etc.) are only in asset bundles. This means `GameParameters.eMapClass` can't be set to a meaningful value. Map scripts may check this — need to verify if `MapClassType.NONE` works or if we need to populate these entries. As a workaround, we might need to add a custom `mapClass-add.xml` with the entries we need.

6. **DLC content requirements.** Some map scripts (Desert, Rejuvenation, Desolation) may require DLC-specific content. If the required content isn't available, the script might fail. Need to handle gracefully.

### Open questions

- **Can `MapParameters.iWidth/iHeight` be left at -1?** The script's `SetMapSize()` in `InitMapData()` should compute these from `eMapSize`, but this needs verification.
- **Does `Build()` work without any PlayerParameters?** Some map scripts check `HumanPlayers` and `NumTeams`. Need at least minimal player setup.
- **Thread safety** — can we generate multiple maps in parallel, or does the Infos/Factory setup have static state?

## Verification Plan

1. **Build the project** — confirm all DLL references resolve and the project compiles
2. **Test Infos loading** — instantiate `ModSettings` with our stubs, verify `Infos` loads without exceptions, spot-check a few values (e.g., `infos.terrain(TerrainType.TERRAIN_WATER).mzType == "TERRAIN_WATER"`)
3. **Test simplest map script** — try `MapScriptContinent` with small size, verify `Build()` returns true
4. **Validate output** — generate a map XML, load it in Old World via the map editor or as a premade map to confirm it's playable
5. **Test each map script** — iterate through all 20 scripts, verify each generates successfully
6. **Batch generation** — generate 10+ maps with same settings, verify different seeds produce different maps
