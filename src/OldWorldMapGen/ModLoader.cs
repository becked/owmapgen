using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;

namespace OldWorldMapGen
{
    public static class ModLoader
    {
        public class ModInfo
        {
            public string DisplayName;
            public string Author;
            public string Tags;
            public string ModDir;
            public string InfosDir;
            public List<string> DllPaths = new List<string>();
            public List<string> ModDependencies = new List<string>();
        }

        public static string GetModsDirectory()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Library", "Application Support", "OldWorld", "Mods");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OldWorld", "Mods");
            }
            else
            {
                // Linux
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    ".config", "OldWorld", "Mods");
            }
        }

        public static ModInfo ParseModInfo(string modDir)
        {
            string modInfoPath = Path.Combine(modDir, "ModInfo.xml");
            if (!File.Exists(modInfoPath))
                return null;

            var info = new ModInfo { ModDir = modDir };

            try
            {
                var doc = new XmlDocument();
                doc.Load(modInfoPath);

                var root = doc.DocumentElement;
                if (root == null) return null;

                info.DisplayName = root.SelectSingleNode("displayName")?.InnerText ?? Path.GetFileName(modDir);
                info.Author = root.SelectSingleNode("author")?.InnerText ?? "";
                info.Tags = root.SelectSingleNode("tags")?.InnerText ?? "";

                var depsNode = root.SelectSingleNode("modDependencies");
                if (depsNode != null)
                {
                    foreach (XmlNode child in depsNode.ChildNodes)
                    {
                        string dep = child.InnerText?.Trim();
                        if (!string.IsNullOrEmpty(dep))
                            info.ModDependencies.Add(dep);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to parse {modInfoPath}: {ex.Message}");
                return null;
            }

            // Discover DLLs in mod root
            try
            {
                foreach (string dll in Directory.GetFiles(modDir, "*.dll"))
                    info.DllPaths.Add(dll);
            }
            catch { }

            // Check for Infos directory
            string infosDir = Path.Combine(modDir, "Infos");
            if (Directory.Exists(infosDir))
                info.InfosDir = infosDir;

            return info;
        }

        public static List<ModInfo> ListAvailableMods()
        {
            var mods = new List<ModInfo>();
            string modsDir = GetModsDirectory();

            if (!Directory.Exists(modsDir))
                return mods;

            foreach (string dir in Directory.GetDirectories(modsDir))
            {
                var info = ParseModInfo(dir);
                if (info != null)
                    mods.Add(info);
            }

            mods.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return mods;
        }

        public static ModInfo ResolveMod(string nameOrPath)
        {
            // If it's a path that exists, use directly
            if (Directory.Exists(nameOrPath))
            {
                var info = ParseModInfo(nameOrPath);
                if (info != null) return info;
                Console.Error.WriteLine($"Error: Directory '{nameOrPath}' exists but has no ModInfo.xml.");
                return null;
            }

            // Search mods directory
            string modsDir = GetModsDirectory();
            if (!Directory.Exists(modsDir))
            {
                Console.Error.WriteLine($"Error: Mods directory not found: {modsDir}");
                return null;
            }

            // Try exact directory name match (case-insensitive)
            foreach (string dir in Directory.GetDirectories(modsDir))
            {
                if (string.Equals(Path.GetFileName(dir), nameOrPath, StringComparison.OrdinalIgnoreCase))
                {
                    var info = ParseModInfo(dir);
                    if (info != null) return info;
                }
            }

            // Try display name match
            foreach (string dir in Directory.GetDirectories(modsDir))
            {
                var info = ParseModInfo(dir);
                if (info != null && string.Equals(info.DisplayName, nameOrPath, StringComparison.OrdinalIgnoreCase))
                    return info;
            }

            Console.Error.WriteLine($"Error: Mod '{nameOrPath}' not found in {modsDir}");
            return null;
        }

        public static List<ModInfo> LoadMods(List<string> modSpecs)
        {
            // Resolve all mods first
            var resolved = new List<ModInfo>();
            foreach (string spec in modSpecs)
            {
                var info = ResolveMod(spec);
                if (info == null)
                    continue;
                resolved.Add(info);
            }

            if (resolved.Count == 0)
                return resolved;

            // Order by dependencies (dependencies first)
            var ordered = OrderByDependencies(resolved);

            // Load DLLs
            foreach (var mod in ordered)
            {
                Console.Error.WriteLine($"Loading mod: {mod.DisplayName}");

                foreach (string dllPath in mod.DllPaths)
                {
                    try
                    {
                        Assembly.LoadFrom(dllPath);
                        Console.Error.WriteLine($"  Loaded assembly: {Path.GetFileName(dllPath)}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Warning: Failed to load {Path.GetFileName(dllPath)}: {ex.Message}");
                    }
                }

                if (mod.InfosDir != null)
                    Console.Error.WriteLine($"  Infos directory: {mod.InfosDir}");
            }

            return ordered;
        }

        private static List<ModInfo> OrderByDependencies(List<ModInfo> mods)
        {
            if (mods.Count <= 1)
                return mods;

            // Build lookup by directory name (mods reference each other by directory name)
            var byDirName = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in mods)
                byDirName[Path.GetFileName(mod.ModDir)] = mod;

            var ordered = new List<ModInfo>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Visit(ModInfo mod)
            {
                string key = Path.GetFileName(mod.ModDir);
                if (visited.Contains(key)) return;
                visited.Add(key);

                foreach (string dep in mod.ModDependencies)
                {
                    if (byDirName.TryGetValue(dep, out var depMod))
                        Visit(depMod);
                }

                ordered.Add(mod);
            }

            foreach (var mod in mods)
                Visit(mod);

            return ordered;
        }
    }
}
