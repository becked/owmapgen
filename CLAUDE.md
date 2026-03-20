# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OldWorldMapGen is a C# console application that generates Old World game maps by loading and executing the game's actual map generation DLLs outside of Unity. It uses Harmony 2 for runtime IL patching to stub out Unity engine methods that aren't available in a headless context.

## Build

Requires .NET SDK and the game installed. The build needs the game directory to resolve managed DLL references:

```bash
# macOS (typical Steam path)
dotnet build src/OldWorldMapGen -p:GameDir="$HOME/Library/Application Support/Steam/steamapps/common/Old World"

# Windows
dotnet build src/OldWorldMapGen -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Old World"
```

Target framework is net472. Game DLLs (`TenCrowns.GameCore.dll`, `Mohawk.SystemCore.dll`, `UnityEngine.CoreModule.dll`) are referenced but not copied to output (`<Private>false</Private>`).

## Run

```bash
# macOS/Linux (via wrapper script that suppresses Mono JIT warnings)
./owmapgen --script "Continent" --size medium --players 4

# Direct
mono src/OldWorldMapGen/bin/OldWorldMapGen.exe --script "Continent" --size medium --players 4
```

Key CLI flags: `--list-scripts`, `--list-options <script>`, `--game-dir <path>`, `--output <path>`, `--count <n>`, `--seed <n>`, `--mod <name|path>`, `--list-mods`.

## Testing

No test framework. Verify changes by running map generation and inspecting XML output in `./maps/`.

## Architecture

**Entry point:** `src/OldWorldMapGen/Program.cs` (~816 lines) — handles CLI parsing, game directory detection, assembly loading, Infos database initialization, map generation loop, and output.

**Initialization sequence** (order matters):
1. Parse CLI arguments
2. Resolve game directory (auto-detects from platform-specific Steam paths)
3. Register assembly resolution handler for game DLLs
4. Apply Harmony patches to stub Unity methods
5. Load game assemblies via reflection
6. Load mod DLLs (if `--mod` specified)
7. Apply game-specific patches (Perlin noise transpiler)
8. Create `ModSettings` with stub implementations (FileSystemXMLLoader merges mod XML)
9. Load Infos database (reflection-discovers map script classes, including mod scripts)
10. Build normalized script lookup table
11. Run map generation with retry logic

**Stub/shim layer** — minimal implementations that replace Unity-dependent systems:
- `ModLoader.cs`: Discovers mods from platform-specific mods directory, parses `ModInfo.xml`, loads mod DLLs. Handles dependency ordering.
- `FileSystemXMLLoader.cs`: Reads game XML data from disk instead of Unity asset bundles. Handles base files + DLC add-on merging (e.g., `terrain.xml` + `terrain-btt.xml`). When mods are loaded, implements `GetModdedXML()` and `GetChangedXML()` to merge mod XML using the game's `ModdedXMLType` flags.
- `HeadlessGameFactory.cs`: Overrides `CreateColorManager()` to return null (skips Unity color space conversion).
- `StubModPath.cs` / `StubUserScriptManager.cs`: No-op implementations for mod system interfaces.
- `UnityPatches.cs`: Harmony patches — stubs `Debug.Log`, `Application.Quit`, and transpiles `Mathf.PerlinNoise` to a managed Perlin noise implementation (`MathfShim`).
- `MapWriter.cs`: Serializes tile data to XML using the game's `TileData.writeXML()`.

**Key patterns:**
- CLI argument matching is flexible: case-insensitive, spaces/hyphens/underscores are interchangeable.
- Heavy use of reflection to discover map scripts, resolve options, and instantiate types from game DLLs.
- `Console.Error` for diagnostics/warnings, `Console.Out` for user-facing results and info output.
- Map generation retries automatically (up to `MAP_BUILD_MAX_ATTEMPTS`) with modified seeds on failure.

## Cross-Platform

The .csproj uses MSBuild conditions to resolve managed DLL paths per platform:
- **macOS:** `{GameDir}/OldWorld.app/Contents/Resources/Data/Managed` (runs on Mono)
- **Windows:** `{GameDir}/OldWorld_Data/Managed` (runs on .NET Framework)
- **Linux:** `{GameDir}/OldWorld_Data/Managed` (runs on Mono)
