// Services/Mx3ExportService.cs
using System.IO;
using System.Globalization;
using System.Xml;
using FxVolatilityImport.Models;

namespace FxVolatilityImport.Services
{
    /// <summary>
    /// Skriver MX3-importfilerna i exakt samma format som legacy-appen.
    /// Alla tal formateras med InvariantCulture – annars blir det "7,441" på en dator med svenska
    /// regionala inställningar, vilket MX3 inte kan läsa.
    /// </summary>
    public class Mx3ExportService
    {
        public const string DefaultOutputDir = @"\\sto-file23.fspa.myntet.se\NTSHARE\MX3_INTRA_DAY_MARKETDATA\";
        public const string AtmFileName = "update_fxvols_ps.xml";
        public const string SmileFileName = "update_fxvols_smile.xml";

        private const string Xmlns = "XmlCache";
        private const string Xmlmp = "mx.MarketParameters";

        private readonly string _outputDir;

        public Mx3ExportService(string? outputDir = null)
        {
            _outputDir = outputDir ?? DefaultOutputDir;
        }

        public string OutputDir => _outputDir;
        public string AtmFilePath => Path.Combine(_outputDir, AtmFileName);
        public string SmileFilePath => Path.Combine(_outputDir, SmileFileName);

        public void ExportAtm(IReadOnlyList<VolatilityTenor> data)
        {
            const string xmlfx = "mx.MarketParameters.Forex";
            const string xmlfxvl = "mx.MarketParameters.Forex.Volatilities";

            var xmlDoc = new XmlDocument();
            var dateNode = CreateHeader(xmlDoc);

            var forexNode = xmlDoc.CreateElement("fx", "forex", xmlfx);
            dateNode.AppendChild(forexNode);

            var volNode = xmlDoc.CreateElement("fxvl", "volatility", xmlfxvl);
            forexNode.AppendChild(volNode);

            foreach (var pair in data.Select(d => d.CurrencyPair).Distinct())
            {
                var ccyNode = xmlDoc.CreateElement("fxvl", "pair", xmlfxvl);
                AddAttribute(xmlDoc, ccyNode, "value", CurrencyPairMapper.ToMx3Format(pair));
                volNode.AppendChild(ccyNode);

                foreach (var tenor in data.Where(d => d.CurrencyPair == pair))
                {
                    var tenorNode = xmlDoc.CreateElement("fxvl", "maturity", xmlfxvl);
                    AddAttribute(xmlDoc, tenorNode, "value", ToMx3Tenor(tenor.Tenor));
                    ccyNode.AppendChild(tenorNode);

                    var bidNode = xmlDoc.CreateElement("mp", "bid", Xmlmp);
                    bidNode.InnerText = Format(tenor.AtmBid);
                    tenorNode.AppendChild(bidNode);

                    var askNode = xmlDoc.CreateElement("mp", "ask", Xmlmp);
                    askNode.InnerText = Format(tenor.AtmAsk);
                    tenorNode.AppendChild(askNode);
                }
            }

            xmlDoc.Save(AtmFilePath);
        }

        public void ExportSmile(IReadOnlyList<VolatilityTenor> data)
        {
            const string xmlfx = "mx.MarketParameters.Rates";
            const string xmlfxsm = "mx.MarketParameters.Rates.Smile";

            var xmlDoc = new XmlDocument();
            var dateNode = CreateHeader(xmlDoc);

            var forexNode = xmlDoc.CreateElement("fx", "forex", xmlfx);
            dateNode.AppendChild(forexNode);

            var smileNode = xmlDoc.CreateElement("fxsm", "smile", xmlfxsm);
            forexNode.AppendChild(smileNode);

            foreach (var pair in data.Select(d => d.CurrencyPair).Distinct())
            {
                var ccyNode = xmlDoc.CreateElement("fxsm", "pair", xmlfxsm);
                AddAttribute(xmlDoc, ccyNode, "value", CurrencyPairMapper.ToMx3Format(pair));
                smileNode.AppendChild(ccyNode);

                foreach (var tenor in data.Where(d => d.CurrencyPair == pair))
                {
                    var tenorNode = xmlDoc.CreateElement("fxsm", "maturity", xmlfxsm);
                    AddAttribute(xmlDoc, tenorNode, "value", ToMx3Tenor(tenor.Tenor));
                    ccyNode.AppendChild(tenorNode);

                    // RR är redan teckenjusterad för inverterade par i BloombergService
                    AddSmileOrdinate(xmlDoc, tenorNode, "10.000000000", Format(tenor.RR10D), Format(tenor.BF10D), xmlfxsm);
                    AddSmileOrdinate(xmlDoc, tenorNode, "25.000000000", Format(tenor.RR25D), Format(tenor.BF25D), xmlfxsm);
                }
            }

            xmlDoc.Save(SmileFilePath);
        }

        /// <summary>Bygger XmlCache → XmlCacheArea → nickName → date och returnerar date-noden.</summary>
        private static XmlElement CreateHeader(XmlDocument xmlDoc)
        {
            var headNode = xmlDoc.CreateElement("xc", "XmlCache", Xmlns);
            AddAttribute(xmlDoc, headNode, "action", "Update");
            xmlDoc.AppendChild(headNode);

            var areaNode = xmlDoc.CreateElement("xc", "XmlCacheArea", Xmlns);
            AddAttribute(xmlDoc, areaNode, "value", "MarketParameters");
            headNode.AppendChild(areaNode);

            var nickNode = xmlDoc.CreateElement("mp", "nickName", Xmlmp);
            AddAttribute(xmlDoc, nickNode, "value", "FO");
            areaNode.AppendChild(nickNode);

            var dateNode = xmlDoc.CreateElement("mp", "date", Xmlmp);
            AddAttribute(xmlDoc, dateNode, "value", DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            nickNode.AppendChild(dateNode);

            return dateNode;
        }

        private static void AddSmileOrdinate(XmlDocument doc, XmlNode parent, string deltaValue,
            string rrValue, string bfValue, string xmlfxsm)
        {
            var ordinateNode = doc.CreateElement("fxsm", "ordinate", xmlfxsm);
            AddAttribute(doc, ordinateNode, "value", deltaValue);
            AddAttribute(doc, ordinateNode, "type", "Fields");
            parent.AppendChild(ordinateNode);

            AddSmileField(doc, ordinateNode, "fxrrAsk", rrValue);
            AddSmileField(doc, ordinateNode, "fxrrBid", rrValue);
            AddSmileField(doc, ordinateNode, "fxstrAsk", bfValue);
            AddSmileField(doc, ordinateNode, "fxstrBid", bfValue);
        }

        private static void AddSmileField(XmlDocument doc, XmlNode parent, string fieldName, string value)
        {
            var node = doc.CreateElement("mp", fieldName, Xmlmp);
            node.InnerText = value;
            AddAttribute(doc, node, "keyFormat", "N");
            AddAttribute(doc, node, "userID", "13");
            AddAttribute(doc, node, "type", "Field");
            parent.AppendChild(node);
        }

        private static void AddAttribute(XmlDocument doc, XmlNode node, string name, string value)
        {
            var attr = doc.CreateAttribute("xc", name, Xmlns);
            attr.Value = value;
            node.Attributes!.Append(attr);
        }

        private static string ToMx3Tenor(string tenor) => tenor == "ON" ? "O/N" : tenor;

        private static string Format(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    }
}