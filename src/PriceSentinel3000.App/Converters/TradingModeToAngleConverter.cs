using System.Globalization;
using System.Windows.Data;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Converters;

public sealed class TradingModeToAngleConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is TradingMode mode
            ? mode switch
            {
                TradingMode.Replay => -55d,
                TradingMode.Simulation => 0d,
                TradingMode.Live => 55d,
                _ => 0d,
            }
            : 0d;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
