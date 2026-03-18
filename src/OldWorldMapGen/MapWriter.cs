using System.Text;
using System.Xml;
using TenCrowns.GameCore;

namespace OldWorldMapGen
{
    public static class MapWriter
    {
        public static void Write(string outputPath, IMapScriptInterface mapScript, Infos infos)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = Encoding.UTF8
            };

            using (var writer = XmlWriter.Create(outputPath, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("Root");
                writer.WriteAttributeString("MapWidth", mapScript.MapWidth.ToString());
                writer.WriteAttributeString("MinLatitude", mapScript.MinLatitude.ToString());
                writer.WriteAttributeString("MaxLatitude", mapScript.MaxLatitude.ToString());
                writer.WriteAttributeString("MapEdgesSafe", mapScript.MapEdgesSafe.ToString());
                writer.WriteAttributeString("MinCitySiteDistance", mapScript.MinCitySiteDistance.ToString());

                foreach (var tile in mapScript.GetTileData())
                {
                    tile.writeXML(infos, writer, null, OccurrenceType.NONE);
                    writer.WriteEndElement(); // close <Tile> opened by writeXML
                }

                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }
    }
}
