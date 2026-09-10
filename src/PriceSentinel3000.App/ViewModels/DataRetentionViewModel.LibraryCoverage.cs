namespace PriceSentinel3000.App.ViewModels;

public sealed partial class DataRetentionViewModel
{
    private long? _libraryCoverageClockSlot;

    internal void RefreshLibraryCoverage()
    {
        if (_disposed) return;
        DateTimeOffset now = _clock.GetUtcNow();
        long slot = now.UtcTicks / (15 * TimeSpan.TicksPerSecond);
        if (_libraryCoverageClockSlot == slot) return;
        _libraryCoverageClockSlot = slot;
        RefreshJobRows(now);
        if (Datasets.Count == 0 || LibraryDays.Count == 0) return;

        // The table already owns validated metadata. Advancing the clock must not
        // scan files, request history, or clear the user's selection and sorting.
        var summaries = LibraryDaySummary.Create(Datasets, now)
            .ToDictionary(day => (day.Symbol, day.TradingDate));
        LibraryDaySummary? selected = SelectedLibraryDay;
        for (int index = 0; index < LibraryDays.Count; index++)
        {
            LibraryDaySummary previous = LibraryDays[index];
            if (summaries.TryGetValue((previous.Symbol, previous.TradingDate), out LibraryDaySummary? next) &&
                next != previous)
                LibraryDays[index] = next;
        }
        if (selected is not null && summaries.TryGetValue((selected.Symbol, selected.TradingDate), out LibraryDaySummary? selection))
        {
            LibraryDaySummary replacement = LibraryDays.First(day => day.Symbol == selection.Symbol && day.TradingDate == selection.TradingDate);
            if (!ReferenceEquals(SelectedLibraryDay, replacement))
            {
                SelectedLibraryDay = replacement;
                Changed(nameof(SelectedLibraryDay));
            }
        }
    }
}
