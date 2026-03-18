# OldWorldMapGen

A standalone command-line tool that generates random [Old World](https://store.steampowered.com/app/597180/Old_World/) maps by running the game's actual map generation code outside of Unity. It references the game's managed DLLs directly, produces the same map scripts and options available in-game, and outputs XML map files that can be loaded as premade maps.

## Quick Start

**Prerequisites:** [.NET SDK](https://dotnet.microsoft.com/download) (for building), Old World installed via Steam. macOS/Linux users also need [Mono](https://www.mono-project.com/download/stable/).

```bash
# Build
dotnet build src/OldWorldMapGen/ -p:GameDir="/path/to/Old World"

# macOS/Linux (wrapper script suppresses Mono runtime warnings)
./owmapgen --script Continent --size medium --players 5

# Windows
OldWorldMapGen.exe --script Continent --size medium --players 5
```

Output is written to `./maps/` by default.

## Platform Support

| Platform | Runtime | How to run |
|----------|---------|------------|
| macOS | Mono | `./owmapgen` wrapper script |
| Linux | Mono | `./owmapgen` wrapper script |
| Windows | .NET Framework 4.7.2 (built into Windows 10+) | Run `OldWorldMapGen.exe` directly |

The `owmapgen` wrapper script filters harmless Mono JIT warnings that are printed to stdout when loading Unity assemblies. These warnings do not affect functionality. You can also run `mono src/OldWorldMapGen/bin/OldWorldMapGen.exe` directly if you prefer.

Windows support has not been tested but should work since the tool targets the same .NET Framework version as the game. If you test it on Windows, please report your results.

## Build

The build requires pointing `GameDir` at your Old World game installation so the project can reference the game's managed DLLs:

```bash
# macOS (Steam)
dotnet build src/OldWorldMapGen/ \
  -p:GameDir="$HOME/Library/Application Support/Steam/steamapps/common/Old World"

# Windows (Steam)
dotnet build src\OldWorldMapGen\ ^
  -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Old World"

# Linux (Steam)
dotnet build src/OldWorldMapGen/ \
  -p:GameDir="$HOME/.steam/steam/steamapps/common/Old World"
```

The game directory is auto-detected at runtime (checking default Steam paths), so `--game-dir` is only needed if you installed the game to a non-standard location.

## Usage

```
OldWorldMapGen [options]

Required:
  --script <name>           Map script (e.g., Continent, "Inland Sea", Donut)
  --size <size>             smallest|tiny|small|medium|large|huge
  --players <n>             Number of players (2-7)

Optional:
  --game-dir <path>         Game install path (auto-detected if not specified)
  --output <path>           Output directory (default: ./maps/)
  --count <n>               Number of maps to generate (default: 1)
  --list-scripts            List available map scripts and exit
  --list-options <script>   List options for a script and exit

Advanced:
  --seed <n>                Map seed 1-99999999 (default: random)
```

### Map Scripts

Use `--list-scripts` to see all available map types. Script names are matched flexibly -- case-insensitive, spaces/hyphens/underscores are interchangeable:

```
--script Continent
--script "Inland Sea"
--script inland-sea
--script inlandsea
```

### Toggle Options

These are on/off flags. Options marked (MP only) are designed for multiplayer maps.

```
  --mirror                  Mirror map (MP only)
  --point-symmetry          Point symmetry (MP only)
  --connected-starts        Connected starting positions (MP only)
  --fair-starts             Fair starting positions
  --king-of-the-hill        King of the hill
  --good-start-resources    Good resources at start
  --force-random-tribes     Randomize tribe placement
  --extra-mountains         Extra mountain ranges
  --extra-water             Extra water features
```

### Multi-Choice Options

These accept a value from a fixed set of choices:

```
  --resources <v>           high|medium|low|random (default: medium)
  --city-density <v>        high|medium|low|random
  --city-number <v>         high|medium|low|single|random
```

### Script-Specific Options

Each map script has its own set of options (terrain type, tribe placement, water size, etc.). Use `--list-options <script>` to see what's available:

```bash
./owmapgen --list-options Continent
```

Set them with `--map-option KEY=VALUE`, which can be repeated:

```bash
./owmapgen --script Continent --size medium --players 5 \
  --map-option MAP_OPTIONS_MULTI_CONTINENT_TERRAIN=MAP_OPTION_CONTINENT_TERRAIN_TUNDRA \
  --map-option MAP_OPTIONS_MULTI_CONTINENT_TRIBES=MAP_OPTION_CONTINENT_TRIBES_QUADRANTS
```

### Examples

```bash
# Basic continent map
./owmapgen --script Continent --size medium --players 5

# Multiplayer mirror map
./owmapgen --script Donut --size large --players 4 --mirror

# Batch generate 5 archipelago maps with high resources
./owmapgen --script Archipelago --size huge --players 6 --count 5 --resources high

# Reproducible generation with explicit seed
./owmapgen --script "Inland Sea" --size small --players 4 --seed 42
```

## How It Works

The tool runs the game's own map generation code (from `TenCrowns.GameCore.dll`) outside of the Unity engine. This means maps are generated using the exact same algorithms, terrain rules, and placement logic as the game itself.

```
┌─────────────────────────────────────────────────────────┐
│  OldWorldMapGen                                         │
│                                                         │
│  ┌────────────────┐  ┌───────────────────────────────┐  │
│  │ CLI (Program)  │  │ Stubs & Shims                 │  │
│  │ - parse args   │  │ - FileSystemXMLLoader          │  │
│  │ - setup params │  │     reads XML from disk        │  │
│  │ - run script   │  │ - StubModPath / StubScriptMgr │  │
│  │ - write XML    │  │     no-op game interfaces      │  │
│  └───────┬────────┘  │ - HeadlessGameFactory         │  │
│          │           │     skips ColorManager          │  │
│          │           │ - UnityPatches (Harmony)       │  │
│          │           │     stubs Debug.Log, patches   │  │
│          │           │     Mathf.PerlinNoise           │  │
│          │           └───────────────┬───────────────┘  │
│          ▼                           ▼                   │
│  ┌───────────────────────────────────────────────────┐  │
│  │ Game DLLs (referenced, not modified)              │  │
│  │                                                   │  │
│  │ TenCrowns.GameCore.dll                            │  │
│  │   MapScriptContinent, MapScriptDonut, ...         │  │
│  │   Infos, ModSettings, GameFactory                 │  │
│  │   MapBuilder, TileData, NoiseGenerator            │  │
│  │                                                   │  │
│  │ Mohawk.SystemCore.dll                             │  │
│  │   Fractal, RandomStruct, DictionaryList           │  │
│  │                                                   │  │
│  │ UnityEngine.CoreModule.dll                        │  │
│  │   Vector3, Mathf (native calls patched out)       │  │
│  └───────────────────────────────────────────────────┘  │
│                                                         │
│  Reads: <game-dir>/Reference/XML/Infos/ (338 XML files) │
│  Writes: <output-dir>/<script>_<size>_<seed>.xml        │
└─────────────────────────────────────────────────────────┘
```

### Initialization

The game's `Infos` database must be loaded before any map script can run. In the normal game, this flows through several Unity-dependent objects. The tool replaces these with lightweight stubs:

1. **FileSystemXMLLoader** implements the game's `IInfosXMLLoader` interface, reading XML data files directly from `Reference/XML/Infos/` on disk instead of Unity asset bundles. This is the only stub with real logic -- it handles the game's filename matching rules for base files and DLC add-on files (e.g., `terrain.xml` + `terrain-btt.xml`).

2. **StubModPath** and **StubUserScriptManager** provide no-op implementations of the mod system interfaces that `ModSettings` requires. The script manager wires up a `HeadlessGameFactory`, which is identical to the game's default `GameFactory` except it skips `ColorManager` creation (which requires Unity's native color space conversion).

3. **UnityPatches** uses [Harmony](https://github.com/pardeike/Harmony) to handle Unity engine methods that require the native Unity runtime:
   - `Debug.Log`, `Debug.LogError`, `Debug.LogWarning` are patched to no-ops (the game code calls these during initialization and error handling)
   - `Mathf.PerlinNoise` is an `[InternalCall]` native method that can't be directly patched, so a Harmony transpiler rewrites the IL in `NoiseGenerator.GetPerlinOctaves` to call a managed Perlin noise implementation instead

4. After initialization, the game's `Infos` system uses reflection to discover all map script classes in the loaded assemblies and builds the script registry dynamically. The tool leverages this -- no hardcoded script list.

### Map Generation

Once `Infos` is loaded, the tool:

1. Creates a `GameParameters` with the requested seed, map size, and player configuration
2. Wires CLI options into the game's `mapMapMultiOptions` and `mapMapSingleOptions` dictionaries
3. Instantiates the requested map script via reflection (same pattern as `MapBuilder.assignMapScript`)
4. Calls `Build()`, which executes the game's 27-stage generation pipeline: land generation, mountains, rivers, lakes, terrain, vegetation, city sites, resources, tribes, and geographic naming
5. If `Build()` fails (e.g., not enough valid city locations), retries with a modified seed, just like the game does
6. Serializes the tile data to XML using the game's own `TileData.writeXML()` method

### Seed Reproducibility

The same seed always produces the same map from this tool. However, maps will differ from the game's output for the same seed because `Mathf.PerlinNoise` is replaced with a managed implementation (Unity's native implementation is compiled into the engine runtime and cannot be extracted). The replacement uses Ken Perlin's canonical improved noise algorithm with the standard permutation table.
