namespace PriceSentinel3000.App.ViewModels;

public sealed record BufferSegmentViewModel(int Number)
{
    public string Label => $"M{Number}";
    public string State => "EMPTY";
}
