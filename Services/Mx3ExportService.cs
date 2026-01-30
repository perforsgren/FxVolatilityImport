// Services/Mx3ExportService.cs
using System.IO;
using System.Xml;
using FxVolatilityImport.Models;

namespace FxVolatilityImport.Services
{
    public class Mx3ExportService
    {
        private readonly string _outputDir =
            @"\\sto-file23.fspa.myntet.se\NTSHARE\MX3_INTRA_DAY_MARKETDATA\";

        private readonly string[] _tenors =
            { "ON", "1W", "2W", "1M", "2M", "3M", "6M", "1Y", "2Y", "3Y" };

        public string AtmFilePath => Path.Combine(_outputDir, "update_fxvols_ps.xml");
        public string SmileFilePath => Path.Combine(_outputDir, "update_fxvols_smile.xml");

        public void ExportAtm(List<VolatilityTenor> data)
        {
            var xmlDoc = new XmlDocument();
            string xmlns = "XmlCache";
            string xmlmp = "mx.MarketParameters";
            string xmlfx = "mx.MarketParameters.Forex";
            string xmlfxvl = "mx.MarketParameters.Forex.Volatilities";

            var headNode = xmlDoc.CreateElement("xc", "XmlCache", xmlns);
            var headAttr = xmlDoc.CreateAttribute("xc", "action", xmlns);
            headAttr.Value = "Update";
            headNode.Attributes.Append(headAttr);
            xmlDoc.AppendChild(headNode);

            var areaNode = xmlDoc.CreateElement("xc", "XmlCacheArea", xmlns);
            var areaAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
            areaAttr.Value = "MarketParameters";
            areaNode.Attributes.Append(areaAttr);
            headNode.AppendChild(areaNode);

            var nickNode = xmlDoc.CreateElement("mp", "nickName", xmlmp);
            var nickAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
            nickAttr.Value = "FO";
            nickNode.Attributes.Append(nickAttr);
            areaNode.AppendChild(nickNode);

            var dateNode = xmlDoc.CreateElement("mp", "date", xmlmp);
            var dateAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
            dateAttr.Value = DateTime.Today.ToString("yyyyMMdd");
            dateNode.Attributes.Append(dateAttr);
            nickNode.AppendChild(dateNode);

            var forexNode = xmlDoc.CreateElement("fx", "forex", xmlfx);
            dateNode.AppendChild(forexNode);

            var volNode = xmlDoc.CreateElement("fxvl", "volatility", xmlfxvl);
            forexNode.AppendChild(volNode);

            var pairs = data.Select(d => d.CurrencyPair).Distinct().ToList();

            foreach (var pair in pairs)
            {
                var ccyNode = xmlDoc.CreateElement("fxvl", "pair", xmlfxvl);
                var ccyAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
                ccyAttr.Value = CurrencyPairMapper.ToMx3Format(pair);
                ccyNode.Attributes.Append(ccyAttr);
                volNode.AppendChild(ccyNode);

                var tenorData = data.Where(d => d.CurrencyPair == pair).ToList();

                foreach (var tenor in tenorData)
                {
                    var tenorNode = xmlDoc.CreateElement("fxvl", "maturity", xmlfxvl);
                    var tenorAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
                    tenorAttr.Value = tenor.Tenor == "ON" ? "O/N" : tenor.Tenor;
                    tenorNode.Attributes.Append(tenorAttr);
                    ccyNode.AppendChild(tenorNode);

                    var bidNode = xmlDoc.CreateElement("mp", "bid", xmlmp);
                    bidNode.InnerText = tenor.AtmBid.ToString("0.000");
                    tenorNode.AppendChild(bidNode);

                    var askNode = xmlDoc.CreateElement("mp", "ask", xmlmp);
                    askNode.InnerText = tenor.AtmAsk.ToString("0.000");
                    tenorNode.AppendChild(askNode);
                }
            }

            xmlDoc.Save(AtmFilePath);
        }

        public void ExportSmile(List<VolatilityTenor> data)
        {
            var xmlDoc = new XmlDocument();
            string xmlns = "XmlCache";
            string xmlmp = "mx.MarketParameters";
            string xmlfx = "mx.MarketParameters.Rates";
            string xmlfxsm = "mx.MarketParameters.Rates.Smile";

            var headNode = xmlDoc.CreateElement("xc", "XmlCache", xmlns);
            var headAttr = xmlDoc.CreateAttribute("xc", "action", xmlns);
            headAttr.Value = "Update";
            headNode.Attributes.Append(headAttr);
            xmlDoc.AppendChild(headNode);

            var areaNode = xmlDoc.CreateElement("xc", "XmlCacheArea", xmlns);
            var areaAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
            areaAttr.Value = "MarketParameters";
            areaNode.Attributes.Append(areaAttr);
            headNode.AppendChild(areaNode);

            var nickNode = xmlDoc.CreateElement("mp", "nickName", xmlmp);
            var nickAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
            nickAttr.Value = "FO";
            nickNode.Attributes.Append(nickAttr);
            areaNode.AppendChild(nickNode);

            var dateNode = xmlDoc.CreateElement("mp", "date", xmlmp);
            var dateAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
            dateAttr.Value = DateTime.Today.ToString("yyyyMMdd");
            dateNode.Attributes.Append(dateAttr);
            nickNode.AppendChild(dateNode);

            var forexNode = xmlDoc.CreateElement("fx", "forex", xmlfx);
            dateNode.AppendChild(forexNode);

            var smileNode = xmlDoc.CreateElement("fxsm", "smile", xmlfxsm);
            forexNode.AppendChild(smileNode);

            var pairs = data.Select(d => d.CurrencyPair).Distinct().ToList();

            foreach (var pair in pairs)
            {
                var ccyNode = xmlDoc.CreateElement("fxsm", "pair", xmlfxsm);
                var ccyAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
                ccyAttr.Value = CurrencyPairMapper.ToMx3Format(pair);
                ccyNode.Attributes.Append(ccyAttr);
                smileNode.AppendChild(ccyNode);

                var tenorData = data.Where(d => d.CurrencyPair == pair).ToList();

                foreach (var tenor in tenorData)
                {
                    var tenorNode = xmlDoc.CreateElement("fxsm", "maturity", xmlfxsm);
                    var tenorAttr = xmlDoc.CreateAttribute("xc", "value", xmlns);
                    tenorAttr.Value = tenor.Tenor == "ON" ? "O/N" : tenor.Tenor;
                    tenorNode.Attributes.Append(tenorAttr);
                    ccyNode.AppendChild(tenorNode);

                    // RR är redan justerad i BloombergService, använd direkt
                    AddSmileOrdinate(xmlDoc, tenorNode, "10.000000000",
                        tenor.RR10D.ToString("0.000"),
                        tenor.BF10D.ToString("0.000"),
                        xmlns, xmlmp, xmlfxsm);

                    AddSmileOrdinate(xmlDoc, tenorNode, "25.000000000",
                        tenor.RR25D.ToString("0.000"),
                        tenor.BF25D.ToString("0.000"),
                        xmlns, xmlmp, xmlfxsm);
                }
            }

            xmlDoc.Save(SmileFilePath);
        }

        private void AddSmileOrdinate(XmlDocument doc, XmlNode parent, string deltaValue,
            string rrValue, string bfValue, string xmlns, string xmlmp, string xmlfxsm)
        {
            var ordinateNode = doc.CreateElement("fxsm", "ordinate", xmlfxsm);

            var valAttr = doc.CreateAttribute("xc", "value", xmlns);
            valAttr.Value = deltaValue;
            ordinateNode.Attributes.Append(valAttr);

            var typeAttr = doc.CreateAttribute("xc", "type", xmlns);
            typeAttr.Value = "Fields";
            ordinateNode.Attributes.Append(typeAttr);

            parent.AppendChild(ordinateNode);

            AddSmileField(doc, ordinateNode, "fxrrAsk", rrValue, xmlns, xmlmp);
            AddSmileField(doc, ordinateNode, "fxrrBid", rrValue, xmlns, xmlmp);
            AddSmileField(doc, ordinateNode, "fxstrAsk", bfValue, xmlns, xmlmp);
            AddSmileField(doc, ordinateNode, "fxstrBid", bfValue, xmlns, xmlmp);
        }

        private void AddSmileField(XmlDocument doc, XmlNode parent, string fieldName,
            string value, string xmlns, string xmlmp)
        {
            var node = doc.CreateElement("mp", fieldName, xmlmp);
            node.InnerText = value;

            var formatAttr = doc.CreateAttribute("xc", "keyFormat", xmlns);
            formatAttr.Value = "N";
            node.Attributes.Append(formatAttr);

            var userIdAttr = doc.CreateAttribute("xc", "userID", xmlns);
            userIdAttr.Value = "13";
            node.Attributes.Append(userIdAttr);

            var typeAttr = doc.CreateAttribute("xc", "type", xmlns);
            typeAttr.Value = "Field";
            node.Attributes.Append(typeAttr);

            parent.AppendChild(node);
        }
    }
}
