// Services/ImportSchedule.cs
namespace FxVolatilityImport.Services
{
    /// <summary>
    /// Schemat för de automatiska importerna: vardagar kl HH:15, från 08:15 till och med 16:15.
    /// Ren logik utan tillstånd. Master-instansen jämför "senaste passerade slot" mot
    /// "senaste lyckade slot" i scheduler.json, så en slot missas inte om timern driver
    /// och körs inte två gånger om master byts.
    /// </summary>
    public static class ImportSchedule
    {
        public const int FirstHour = 8;
        public const int LastHour = 16;
        public const int Minute = 15;

        /// <summary>Hur länge efter en slot den fortfarande får köras (efter byte av master, Bloomberg-avbrott m.m.).</summary>
        public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

        /// <summary>Max antal försök per slot, och minsta tid mellan försöken.</summary>
        public const int MaxAttemptsPerSlot = 3;
        public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(3);

        public static string Description => $"Weekdays {FirstHour:00}:{Minute:00}–{LastHour:00}:{Minute:00}, every hour";

        public static bool IsTradingDay(DateTime date)
            => date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;

        /// <summary>Senaste slot som har passerat (≤ now) idag, annars null.</summary>
        public static DateTime? LatestSlot(DateTime now)
        {
            if (!IsTradingDay(now))
                return null;

            for (int hour = LastHour; hour >= FirstHour; hour--)
            {
                var slot = now.Date.AddHours(hour).AddMinutes(Minute);
                if (slot <= now)
                    return slot;
            }

            return null;
        }

        /// <summary>Nästa slot efter now (helger hoppas över).</summary>
        public static DateTime NextSlot(DateTime now)
        {
            for (var day = now.Date; ; day = day.AddDays(1))
            {
                if (!IsTradingDay(day))
                    continue;

                for (int hour = FirstHour; hour <= LastHour; hour++)
                {
                    var slot = day.AddHours(hour).AddMinutes(Minute);
                    if (slot > now)
                        return slot;
                }
            }
        }
    }
}