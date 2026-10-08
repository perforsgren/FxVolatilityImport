// Models/SchedulerLeaseInfo.cs
namespace FxVolatilityImport.Models
{
    /// <summary>
    /// Innehållet i scheduler.json: vem som är master och hur senaste schemalagda import gick.
    /// </summary>
    public class SchedulerLeaseInfo
    {
        public string User { get; set; } = "";
        public string Machine { get; set; } = "";
        public int ProcessId { get; set; }
        public DateTime AcquiredAt { get; set; }
        public DateTime Heartbeat { get; set; }

        /// <summary>Senaste slot (t.ex. 10:15) som importerats utan fel. Förhindrar dubbelkörning vid byte av master.</summary>
        public DateTime? LastSuccessfulSlot { get; set; }

        /// <summary>Slotten (t.ex. 10:15) som senaste körning gällde.</summary>
        public DateTime? LastRunSlot { get; set; }

        public DateTime? LastRunAt { get; set; }
        public bool LastRunOk { get; set; }
        public string LastRunSummary { get; set; } = "";
    }
}