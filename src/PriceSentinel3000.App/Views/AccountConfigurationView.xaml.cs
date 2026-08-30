using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.Views;

public partial class AccountConfigurationView : UserControl
{
    public AccountConfigurationView()
    {
        InitializeComponent();
    }

    private void HandleSymbolTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsKeyboardFocusWithin: true } textBox &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.ScheduleSymbolSuggestionsRefresh(textBox.Text);
        }
    }

    private void HandleSymbolPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.Key is Key.Tab)
        {
            viewModel.DismissSymbolSuggestions();
            return;
        }

        if (!viewModel.IsSymbolSuggestionsOpen ||
            SymbolSuggestionsList.Items.Count == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                MoveSuggestionSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSuggestionSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                AcceptSelectedSuggestion(viewModel);
                e.Handled = true;
                break;
            case Key.Escape:
                viewModel.DismissSymbolSuggestions();
                e.Handled = true;
                break;
        }
    }

    private void HandleSymbolSuggestionClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(SymbolSuggestionsList, source)
                is not ListBoxItem { DataContext: InstrumentSearchResult suggestion })
        {
            return;
        }

        viewModel.AcceptSymbolSuggestion(suggestion);
        SymbolTextBox.Focus();
        SymbolTextBox.CaretIndex = SymbolTextBox.Text.Length;
        e.Handled = true;
    }

    private void HandleSymbolLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (!SymbolSuggestionsPopup.IsMouseOver &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.DismissSymbolSuggestions();
        }
    }

    private void MoveSuggestionSelection(int offset)
    {
        int count = SymbolSuggestionsList.Items.Count;
        int current = SymbolSuggestionsList.SelectedIndex;
        int next = current < 0
            ? offset > 0 ? 0 : count - 1
            : Math.Clamp(current + offset, 0, count - 1);
        SymbolSuggestionsList.SelectedIndex = next;
        SymbolSuggestionsList.ScrollIntoView(SymbolSuggestionsList.SelectedItem);
    }

    private void AcceptSelectedSuggestion(MainViewModel viewModel)
    {
        InstrumentSearchResult? suggestion =
            SymbolSuggestionsList.SelectedItem as InstrumentSearchResult ??
            SymbolSuggestionsList.Items[0] as InstrumentSearchResult;
        if (suggestion is null)
        {
            return;
        }

        viewModel.AcceptSymbolSuggestion(suggestion);
        SymbolTextBox.CaretIndex = SymbolTextBox.Text.Length;
    }
}
