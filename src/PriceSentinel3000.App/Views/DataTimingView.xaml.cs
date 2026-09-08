using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Views;

public partial class DataTimingView : UserControl
{
    private MainViewModel? _subscribedViewModel;
    private DateTime _displayedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public DataTimingView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SubscribeToViewModel();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ReplayCalendarPopup.IsOpen = false;
        UnsubscribeFromViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SubscribeToViewModel();
        }
    }

    private void SubscribeToViewModel()
    {
        UnsubscribeFromViewModel();
        _subscribedViewModel = DataContext as MainViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReplayCalendarPopup.IsOpen &&
            e.PropertyName is nameof(MainViewModel.ReplayCalendarDays) or nameof(MainViewModel.ReplayDate))
        {
            RenderCalendar();
        }
    }

    private void CommitReplayInputs()
    {
        ReplayDateInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        ReplayStartInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        ReplayEndInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private async void OnReplaySettingsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CheckReplayAvailabilityAsync();
        }
    }

    private async void OnCheckReplayAvailability(object sender, RoutedEventArgs e) =>
        await CheckReplayAvailabilityAsync();

    private async Task CheckReplayAvailabilityAsync()
    {
        CommitReplayInputs();
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.CheckReplayAvailabilityAsync();
        }
    }

    private void OnOpenReplayCalendar(object sender, RoutedEventArgs e)
    {
        CommitReplayInputs();
        if (DateTime.TryParseExact(ReplayDateInput.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var selectedDate))
        {
            _displayedMonth = new DateTime(selectedDate.Year, selectedDate.Month, 1);
        }

        ReplayCalendarPopup.DataContext = DataContext;
        ReplayCalendarPopup.IsOpen = !ReplayCalendarPopup.IsOpen;
    }

    private async void OnCalendarOpened(object sender, EventArgs e)
    {
        await LoadDisplayedMonthAsync();
        FocusSelectedCalendarDay();
    }

    private void OnCalendarClosed(object sender, EventArgs e) => ReplayCalendarButton.Focus();

    private async void OnPreviousCalendarMonth(object sender, RoutedEventArgs e) => await ChangeMonthAsync(-1);

    private async void OnNextCalendarMonth(object sender, RoutedEventArgs e) => await ChangeMonthAsync(1);

    private async Task ChangeMonthAsync(int offset)
    {
        if ((offset < 0 && _displayedMonth.Year == 1 && _displayedMonth.Month == 1) ||
            (offset > 0 && _displayedMonth.Year == 9999 && _displayedMonth.Month == 12))
        {
            return;
        }

        _displayedMonth = _displayedMonth.AddMonths(offset);
        await LoadDisplayedMonthAsync();
    }

    private async Task LoadDisplayedMonthAsync()
    {
        var requestedMonth = _displayedMonth;
        RenderCalendar();
        CalendarLoadingText.Text = "Checking saved coverage…";
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.LoadReplayCalendarMonthAsync(requestedMonth);
        }

        if (requestedMonth == _displayedMonth)
        {
            RenderCalendar();
            CalendarLoadingText.Text = "Colors apply to the selected stock and local start/end times.";
        }
    }

    private void RenderCalendar()
    {
        var focusedDay = (Keyboard.FocusedElement as Button)?.Tag as DateOnly?;
        CalendarMonthTitle.Text = _displayedMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        PreviousCalendarMonthButton.IsEnabled = _displayedMonth.Year != 1 || _displayedMonth.Month != 1;
        NextCalendarMonthButton.IsEnabled = _displayedMonth.Year != 9999 || _displayedMonth.Month != 12;
        CalendarDaysGrid.Children.Clear();
        var monthStart = DateOnly.FromDateTime(_displayedMonth);
        var firstDayNumber = monthStart.DayNumber - (int)monthStart.DayOfWeek;
        var viewModel = DataContext as MainViewModel;
        DateOnly.TryParseExact(viewModel?.ReplayDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var selectedDay);

        for (var index = 0; index < 42; index++)
        {
            var dayNumber = firstDayNumber + index;
            if (dayNumber < DateOnly.MinValue.DayNumber || dayNumber > DateOnly.MaxValue.DayNumber)
            {
                CalendarDaysGrid.Children.Add(new Border());
                continue;
            }

            var date = DateOnly.FromDayNumber(dayNumber);
            ReplayCalendarDay? availability = null;
            viewModel?.ReplayCalendarDays.TryGetValue(date, out availability);
            var description = availability?.Description ?? "Not checked. Select this date to check data availability.";
            var (background, foreground, label) = CalendarColors(availability?.Status);
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = date.Day.ToString(CultureInfo.InvariantCulture),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var button = new Button
            {
                Tag = date,
                Content = content,
                Style = (Style)FindResource("CalendarButtonStyle"),
                Background = background,
                Foreground = foreground,
                Opacity = date.Month == monthStart.Month ? 1 : 0.45,
                ToolTip = $"{date:yyyy-MM-dd}: {description}"
            };
            if (date == selectedDay)
            {
                button.BorderBrush = Brushes.White;
                button.BorderThickness = new Thickness(2);
            }
            AutomationProperties.SetName(button, $"{date:yyyy-MM-dd}. {description}");
            button.Click += OnCalendarDayClick;
            CalendarDaysGrid.Children.Add(button);
            if (date == focusedDay)
            {
                button.Focus();
            }
        }
    }

    private static (Brush Background, Brush Foreground, string Label) CalendarColors(string? status) => status switch
    {
        "Disk15" => (ColorBrush(0x17, 0x67, 0x47), Brushes.White, "15D"),
        "Broker15" => (ColorBrush(0x77, 0xD9, 0xAA), ColorBrush(0x09, 0x1C, 0x14), "15B"),
        "Coarse60" => (ColorBrush(0xE8, 0xAB, 0x4D), ColorBrush(0x24, 0x17, 0x06), "30–60s"),
        "Coarse120" => (ColorBrush(0xEC, 0x8B, 0x7C), ColorBrush(0x2A, 0x10, 0x10), "2m"),
        "Partial" => (ColorBrush(0x1F, 0x2B, 0x38), ColorBrush(0xAF, 0xBD, 0xCE), "GAP"),
        "Unavailable" => (ColorBrush(0x1F, 0x2B, 0x38), ColorBrush(0xAF, 0xBD, 0xCE), "—"),
        "Error" => (ColorBrush(0x1F, 0x2B, 0x38), ColorBrush(0xAF, 0xBD, 0xCE), "!"),
        "Checking" => (ColorBrush(0x1F, 0x2B, 0x38), ColorBrush(0xAF, 0xBD, 0xCE), "…"),
        _ => (ColorBrush(0x1F, 0x2B, 0x38), ColorBrush(0xAF, 0xBD, 0xCE), "?")
    };

    private static Brush ColorBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private async void OnCalendarDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateOnly date } && DataContext is MainViewModel viewModel)
        {
            viewModel.ReplayDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ReplayCalendarPopup.IsOpen = false;
            await viewModel.CheckReplayAvailabilityAsync();
        }
    }

    private void FocusSelectedCalendarDay()
    {
        if (!ReplayCalendarPopup.IsOpen)
        {
            return;
        }

        var buttons = CalendarDaysGrid.Children.OfType<Button>().ToArray();
        var selectedDate = (DataContext as MainViewModel)?.ReplayDate;
        var selected = buttons.FirstOrDefault(button => button.Tag is DateOnly date &&
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == selectedDate);
        (selected ?? buttons.FirstOrDefault(button => button.Tag is DateOnly date && date.Day == 1))?.Focus();
    }

    private async void OnCalendarKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ReplayCalendarPopup.IsOpen = false;
            return;
        }

        if (e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            await ChangeMonthAsync(e.Key == Key.PageUp ? -1 : 1);
            FocusSelectedCalendarDay();
            return;
        }

        var offset = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0 };
        if (offset == 0 || Keyboard.FocusedElement is not Button { Tag: DateOnly focusedDate })
        {
            return;
        }

        e.Handled = true;
        var dayNumber = focusedDate.DayNumber + offset;
        if (dayNumber < DateOnly.MinValue.DayNumber || dayNumber > DateOnly.MaxValue.DayNumber)
        {
            return;
        }

        var nextDate = DateOnly.FromDayNumber(dayNumber);
        if (nextDate.Month != _displayedMonth.Month || nextDate.Year != _displayedMonth.Year)
        {
            _displayedMonth = new DateTime(nextDate.Year, nextDate.Month, 1);
            await LoadDisplayedMonthAsync();
        }

        CalendarDaysGrid.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, nextDate))?.Focus();
    }
}
