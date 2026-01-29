// Services/SettingsService.cs
using System.IO;
using System.Text.Json;
using FxVolatilityImport.Models;

namespace FxVolatilityImport.Services
{
    public class SettingsService
    {
        private readonly string _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FxVolatilityImport",
            "settings.json");

        public AppSettings Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save(AppSettings settings)
        {
            settings.LastSaved = DateTime.Now;
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_settingsPath, json);
        }
    }
}
