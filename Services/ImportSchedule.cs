// Services/ImportSchedule.cs
namespace FxVolatilityImport.Services
{
    public enum SlotAction
    {
        /// <summary>Inget att göra just nu.</summary>
        None,
        /// <summary>Starta första körningen för slotten.</summary>
        Start,
        /// <summary>Förra försöket för slotten misslyckades – försök igen.</summary>
        Retry,
        /// <summary>Slotten passerade för länge sedan för att startas (t.ex. appen startades 10:26) – hoppa över den.</summary>
        SkipTooLate
    }

    /// <summary>
    /// Schemat för de automatiska importerna: vardagar kl HH:15, från 08:15 till och med 16:15.
    /// Ren logik utan tillstånd, så den går att testa. Master-instansen anropar Evaluate varje sekund.
    /// </summary>
    public static class ImportSchedule
    {
        public const int FirstHour = 8;
        public const int LastHour = 16;
        public const int Minute = 15;

        /// <summary>
        /// Hur sent efter en slot den FÖRSTA körningen får starta. Täcker att timern driver, att Bloomberg
        /// precis återanslutit och byte av master (tar ca 10 s, eller ca 3 min om masterns Bloomberg dött).
        /// Startas appen senare än så (t.ex. 10:26) körs inte 10:15 – nästa körning blir 11:15.
        /// </summary>
        public static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(5);

        /// <summary>Hur länge ett misslyckat försök får göras om.</summary>
        public static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(15);

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

        /// <summary>
        /// Avgör vad master ska göra just nu.
        /// </summary>
        /// <param name="now">Aktuell tid.</param>
        /// <param name="lastSuccessfulSlot">Senaste slot som importerats utan fel (delas via scheduler.json).</param>
        /// <param name="lastFailedSlot">Slot som (föregående) master försökte men misslyckades med (delas via scheduler.json).</param>
        /// <param name="attemptedSlot">Slot som denna instans senast försökte köra.</param>
        /// <param name="attempts">Antal försök denna instans gjort för attemptedSlot.</param>
        /// <param name="lastAttemptAt">När senaste försöket gjordes.</param>
        public static (SlotAction Action, DateTime? Slot) Evaluate(
            DateTime now,
            DateTime? lastSuccessfulSlot,
            DateTime? lastFailedSlot,
            DateTime? attemptedSlot,
            int attempts,
            DateTime lastAttemptAt)
        {
            var slot = LatestSlot(now);
            if (slot == null)
                return (SlotAction.None, null);

            if (lastSuccessfulSlot.HasValue && lastSuccessfulSlot.Value >= slot.Value)
                return (SlotAction.None, slot);

            var age = now - slot.Value;

            if (attemptedSlot == slot)
            {
                if (attempts >= MaxAttemptsPerSlot || age > RetryWindow || now - lastAttemptAt < RetryDelay)
                    return (SlotAction.None, slot);
                return (SlotAction.Retry, slot);
            }

            // Förra mastern försökte men misslyckades (t.ex. dess Terminal loggades ut) och lämnade över rollen:
            // ta över omförsöken så länge vi är inom RetryWindow, i stället för att hoppa över slotten.
            if (lastFailedSlot == slot)
                return age <= RetryWindow ? (SlotAction.Start, slot) : (SlotAction.None, slot);

            return age <= StartWindow
                ? (SlotAction.Start, slot)
                : (SlotAction.SkipTooLate, slot);
        }
    }
}
