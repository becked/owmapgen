# Changelog

## 1.1.0

### Added
- **Mod support**: Load mods that add custom map scripts, options, and data
  - `--mod <name|path>` flag to load one or more mods (repeatable)
  - `--list-mods` to show all installed mods with author and tags
  - Mod DLLs are loaded automatically; mod XML data (add/change/append) is merged into the Infos database
  - Mod names are matched case-insensitively against directory names and display names
  - Auto-detects platform-specific mods directory (`~/Library/Application Support/OldWorld/Mods/` on macOS, `%APPDATA%/OldWorld/Mods/` on Windows, `~/.config/OldWorld/Mods/` on Linux)
  - Mods are loaded in dependency order when multiple mods are specified

## 1.0.0

Initial release.

- Generate Old World maps from the command line using the game's actual map generation DLLs
- All base game map scripts supported (Continent, Archipelago, Donut, Inland Sea, etc.)
- Full map option support: toggle options, multi-choice options, and script-specific options
- Batch generation with `--count`
- Reproducible maps with `--seed`
- Cross-platform: macOS (Mono), Windows (.NET Framework), Linux (Mono)
- Auto-detects game installation from default Steam paths
- XML output compatible with Old World's premade map format
