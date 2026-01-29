// Services/Mx3ExportService.cs (samma XML-logik som legacy men renare)
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

        public void ExportAtm(List<VolatilityTenor> data)
        {
            // ... samma XML-struktur som createXMLfile() i legacy
            // Sparar till update_fxvols_ps.xml
        }

        public void ExportSmile(List<VolatilityTenor> data)
        {
            // ... samma XML-struktur som createXMLfileRRBF() i legacy
            // Sparar till update_fxvols_smile.xml
        }
    }
}
