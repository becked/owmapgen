using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using TenCrowns.GameCore;

namespace OldWorldMapGen
{
    public class FileSystemXMLLoader : IInfosXMLLoader
    {
        private readonly string xmlDir;
        private readonly Dictionary<string, List<string>> filesByBaseName;
        private readonly List<ModXmlEntry> modXmlEntries = new List<ModXmlEntry>();

        private struct ModXmlEntry
        {
            public string FilePath;
            public string BaseName;     // e.g. "mapOption", "text", "globalsint"
            public ModdedXMLType Type;  // EXACT, ADD, CHANGE, or APPEND
        }

        public FileSystemXMLLoader(string xmlDir)
        {
            this.xmlDir = xmlDir;
            filesByBaseName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in Directory.GetFiles(xmlDir, "*.xml"))
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                if (!filesByBaseName.ContainsKey(fileName))
                    filesByBaseName[fileName] = new List<string>();
                filesByBaseName[fileName].Add(filePath);
            }
        }

        public void AddModInfosDirs(List<string> dirs)
        {
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;

                foreach (string filePath in Directory.GetFiles(dir, "*.xml"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(filePath);
                    var entry = ParseModFileName(fileName, filePath);
                    modXmlEntries.Add(entry);
                }
            }
        }

        private static ModXmlEntry ParseModFileName(string fileName, string filePath)
        {
            // Split on last hyphen to find suffix
            int lastHyphen = fileName.LastIndexOf('-');
            if (lastHyphen > 0 && lastHyphen < fileName.Length - 1)
            {
                string baseName = fileName.Substring(0, lastHyphen);
                string suffix = fileName.Substring(lastHyphen + 1);

                ModdedXMLType type;
                if (suffix.Equals("change", StringComparison.OrdinalIgnoreCase))
                    type = ModdedXMLType.CHANGE;
                else if (suffix.Equals("append", StringComparison.OrdinalIgnoreCase))
                    type = ModdedXMLType.APPEND;
                else
                    // "add", "ADD", or any other suffix (like DLC codes) → ADD
                    type = ModdedXMLType.ADD;

                return new ModXmlEntry { FilePath = filePath, BaseName = baseName, Type = type };
            }

            // No hyphen → exact replacement
            return new ModXmlEntry { FilePath = filePath, BaseName = fileName, Type = ModdedXMLType.EXACT };
        }

        public void ResetCache(bool resetDefaultXML) { }
        public bool IsValidateOverride() => false;
        public IInfoXmlFieldDataProvider GetFieldDataProvider() => null;

        public List<XmlDocument> GetDefaultXML(string resourceName)
        {
            return GetDefaultXML(resourceName, out _);
        }

        public List<XmlDocument> GetDefaultXML(string resourceName, out List<string> xmlPaths)
        {
            xmlPaths = new List<string>();
            var docs = new List<XmlDocument>();

            string baseName = ExtractBaseName(resourceName);

            // Find exact match first
            if (filesByBaseName.TryGetValue(baseName, out var exactPaths))
            {
                foreach (string path in exactPaths)
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(path), baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        var doc = LoadXml(path);
                        if (doc != null)
                        {
                            docs.Add(doc);
                            xmlPaths.Add(path);
                        }
                    }
                }
            }

            // Find ADD files: baseName-suffix.xml where suffix is not "change" or "append"
            string prefix = baseName + "-";
            foreach (var kvp in filesByBaseName)
            {
                string fileName = kvp.Key;
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && fileName.Length > prefix.Length)
                {
                    string suffix = fileName.Substring(prefix.Length);
                    if (!suffix.Equals("change", StringComparison.OrdinalIgnoreCase) &&
                        !suffix.Equals("append", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string path in kvp.Value)
                        {
                            var doc = LoadXml(path);
                            if (doc != null)
                            {
                                docs.Add(doc);
                                xmlPaths.Add(path);
                            }
                        }
                    }
                }
            }

            return docs;
        }

        public List<XmlDocument> GetChangedXML(string resourceName)
        {
            return GetChangedXML(resourceName, out _);
        }

        public List<XmlDocument> GetChangedXML(string resourceName, out List<string> xmlPaths)
        {
            xmlPaths = new List<string>();
            var docs = new List<XmlDocument>();
            string baseName = ExtractBaseName(resourceName);

            foreach (var entry in modXmlEntries)
            {
                if (entry.Type == ModdedXMLType.CHANGE &&
                    string.Equals(entry.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
                {
                    var doc = LoadXml(entry.FilePath);
                    if (doc != null)
                    {
                        docs.Add(doc);
                        xmlPaths.Add(entry.FilePath);
                    }
                }
            }

            return docs;
        }

        public List<XmlDocument> GetModdedXML(string resourceName, ModdedXMLType searchType)
        {
            return GetModdedXML(resourceName, searchType, out _);
        }

        public List<XmlDocument> GetModdedXML(string resourceName, ModdedXMLType searchType, out List<string> xmlPaths)
        {
            xmlPaths = new List<string>();
            var docs = new List<XmlDocument>();
            string baseName = ExtractBaseName(resourceName);

            foreach (var entry in modXmlEntries)
            {
                if (!string.Equals(entry.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Match entry type against search flags
                // ADD_ALWAYS (4) should also match ADD files
                ModdedXMLType effectiveSearch = searchType;
                if ((searchType & ModdedXMLType.ADD_ALWAYS) != 0)
                    effectiveSearch |= ModdedXMLType.ADD;

                if ((entry.Type & effectiveSearch) != 0)
                {
                    var doc = LoadXml(entry.FilePath);
                    if (doc != null)
                    {
                        docs.Add(doc);
                        xmlPaths.Add(entry.FilePath);
                    }
                }
            }

            return docs;
        }

        public XmlDocument GetMergedXmlForAsset(XmlDocument baseDoc, List<string> moddedAssets) => null;

        private static string ExtractBaseName(string resourceName)
        {
            string baseName = resourceName;
            int slashIndex = resourceName.LastIndexOf('/');
            if (slashIndex >= 0)
                baseName = resourceName.Substring(slashIndex + 1);
            return baseName;
        }

        private XmlDocument LoadXml(string path)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                return doc;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to load XML file {path}: {ex.Message}");
                return null;
            }
        }
    }
}
