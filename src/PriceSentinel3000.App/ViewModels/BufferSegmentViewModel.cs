using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.ViewModels;

public sealed class BufferSegmentViewModel(int number) : INotifyPropertyChanged
{
    private string _state = "EMPTY";
    private string _price = "—";
    private string _detail = "WAITING";
    private string _foreground = "#52677F";

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Number { get; } = number;
    public string Label => $"M{Number}";
    public string State => _state;
    public string Price => _price;
    public string Detail => _detail;
    public string Foreground => _foreground;

    public void Update(MinuteBlock block)
    {
        _state = block.Direction.ToString().ToUpperInvariant();
        _price = block.Close is null
            ? "—"
            : block.Close.Value.ToString("$0.00", CultureInfo.InvariantCulture);
        _detail = block.ChangePercent is null
            ? "WAITING"
            : $"{block.ChangePercent.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}% · {block.QuoteCount}Q";
        _foreground = block.Direction switch
        {
            PriceDirection.Up => "#5EE6B1",
            PriceDirection.Down => "#FF8A78",
            PriceDirection.Flat => "#B8C5D4",
            _ => "#52677F",
        };

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Price));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Foreground));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
