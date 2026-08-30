using System.Collections.ObjectModel;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly TimeSpan SymbolSuggestionDebounce =
        TimeSpan.FromMilliseconds(300);
    private const int MaximumSymbolSuggestions = 8;
    private CancellationTokenSource? _symbolSuggestionCancellation;
    private int _symbolSuggestionGeneration;
    private bool _isSymbolSuggestionsOpen;
    private bool _isApplyingSymbolSuggestion;

    public ObservableCollection<InstrumentSearchResult> SymbolSuggestions { get; } = [];

    public bool IsSymbolSuggestionsOpen
    {
        get => _isSymbolSuggestionsOpen;
        set => SetField(ref _isSymbolSuggestionsOpen, value);
    }

    internal void ScheduleSymbolSuggestionsRefresh(string input)
    {
        CancelSymbolSuggestionRefresh();
        ClearSymbolSuggestions();

        string query = input.Trim().ToUpperInvariant();
        if (_isApplyingSymbolSuggestion ||
            !_isMarketDataConnected ||
            query.Length < 2 ||
            IsSessionRunning ||
            _isStartingSession)
        {
            return;
        }

        int generation = ++_symbolSuggestionGeneration;
        var cancellation = new CancellationTokenSource();
        _symbolSuggestionCancellation = cancellation;
        _ = RefreshSymbolSuggestionsAsync(query, generation, cancellation);
    }

    internal void AcceptSymbolSuggestion(InstrumentSearchResult suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        CancelSymbolSuggestionRefresh();

        _isApplyingSymbolSuggestion = true;
        try
        {
            Symbol = suggestion.Symbol;
        }
        finally
        {
            _isApplyingSymbolSuggestion = false;
        }

        ClearSymbolSuggestions();
    }

    internal void DismissSymbolSuggestions()
    {
        CancelSymbolSuggestionRefresh();
        ClearSymbolSuggestions();
    }

    private async Task RefreshSymbolSuggestionsAsync(
        string query,
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(
                SymbolSuggestionDebounce,
                _timeProvider,
                cancellation.Token);
            IReadOnlyList<InstrumentSearchResult> matches =
                await _instrumentSearchSource.SearchAsync(
                    query,
                    cancellation.Token);

            if (cancellation.IsCancellationRequested ||
                generation != _symbolSuggestionGeneration ||
                IsSessionRunning ||
                _isStartingSession ||
                !string.Equals(
                    query,
                    Symbol.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            InstrumentSearchResult[] suggestions = matches
                .Select((match, index) => new { Match = match, Index = index })
                .OrderBy(item => item.Match.Symbol.StartsWith(
                    query,
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(item => item.Index)
                .Select(item => item.Match)
                .Take(MaximumSymbolSuggestions)
                .ToArray();

            SymbolSuggestions.Clear();
            foreach (InstrumentSearchResult suggestion in suggestions)
            {
                SymbolSuggestions.Add(suggestion);
            }

            IsSymbolSuggestionsOpen = SymbolSuggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (generation == _symbolSuggestionGeneration)
            {
                ClearSymbolSuggestions();
            }
        }
        finally
        {
            if (ReferenceEquals(_symbolSuggestionCancellation, cancellation))
            {
                _symbolSuggestionCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void ClearSymbolSuggestions()
    {
        SymbolSuggestions.Clear();
        IsSymbolSuggestionsOpen = false;
    }

    private void CancelSymbolSuggestionRefresh()
    {
        _symbolSuggestionGeneration++;
        CancellationTokenSource? cancellation = _symbolSuggestionCancellation;
        _symbolSuggestionCancellation = null;
        cancellation?.Cancel();
    }
}
