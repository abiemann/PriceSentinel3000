using PriceSentinel3000.Core.Charting;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool _showEma50 = true;
    private bool _showEma100 = true;
    private bool _showEma200 = true;
    private bool _showVwap = true;

    public bool ShowEma50
    {
        get => _showEma50;
        set => SetField(ref _showEma50, value);
    }

    public bool ShowEma100
    {
        get => _showEma100;
        set => SetField(ref _showEma100, value);
    }

    public bool ShowEma200
    {
        get => _showEma200;
        set => SetField(ref _showEma200, value);
    }

    public bool ShowVwap
    {
        get => _showVwap;
        set
        {
            if (SetField(ref _showVwap, value))
                OnPropertyChanged(nameof(VwapAvailabilityText));
        }
    }

    public string VwapAvailabilityText => !ShowVwap ? "VWAP hidden" : ChartPoints.Count == 0
        ? "VWAP: waiting for data"
        : ChartPoints[^1].VwapStatus switch
        {
            VwapStatus.Available => "VWAP: regular session",
            VwapStatus.OutsideSession => "VWAP: outside regular session",
            VwapStatus.MissingVolume => "VWAP: volume unavailable",
            VwapStatus.NoVolume => "VWAP: no traded volume",
            VwapStatus.IncompleteCandle => "VWAP: waiting for completed candle",
            _ => "VWAP: needs complete history from 9:30 ET",
        };
}
