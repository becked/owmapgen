using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using TenCrowns.GameCore;

namespace OldWorldMapGen
{
    public class FileSystemXMLLoader : IInfosXMLLoader
    {
        private readonly string xmlDir;
        private readonly Dictionary<string, List<string>> filesByBaseName;

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

            string baseName = resourceName;
            int slashIndex = resourceName.LastIndexOf('/');
            if (slashIndex >= 0)
                baseName = resourceName.Substring(slashIndex + 1);

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

        public List<XmlDocument> GetChangedXML(string resourceName) => new List<XmlDocument>();
        public List<XmlDocument> GetChangedXML(string resourceName, out List<string> xmlPaths)
        {
            xmlPaths = new List<string>();
            return new List<XmlDocument>();
        }

        public List<XmlDocument> GetModdedXML(string resourceName, ModdedXMLType searchType) => new List<XmlDocument>();
        public List<XmlDocument> GetModdedXML(string resourceName, ModdedXMLType searchType, out List<string> xmlPaths)
        {
            xmlPaths = new List<string>();
            return new List<XmlDocument>();
        }

        public XmlDocument GetMergedXmlForAsset(XmlDocument baseDoc, List<string> moddedAssets) => null;

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
