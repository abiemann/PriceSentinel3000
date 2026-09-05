using System.Windows;
using System.Windows.Controls;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Views;

public partial class TradingConfigurationPanel : UserControl
{
    public TradingConfigurationPanel()
    {
        InitializeComponent();
        AddHandler(Validation.ErrorEvent, new EventHandler<ValidationErrorEventArgs>(HandleValidationError));
        DataContextChanged += HandleDataContextChanged;
        foreach (TextBox input in FindInputs(SessionInputs))
        {
            input.IsEnabledChanged += (_, _) => UpdateConfigurationErrors();
        }
    }

    private void HandleDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel previous)
        {
            previous.ValidateConfigurationInputs = null;
        }

        if (e.NewValue is MainViewModel viewModel)
        {
            viewModel.ValidateConfigurationInputs = CommitInputs;
            UpdateConfigurationErrors();
        }
    }

    private void HandleValidationError(object? sender, ValidationErrorEventArgs e)
    {
        UpdateConfigurationErrors();
    }

    private bool CommitInputs()
    {
        foreach (TextBox input in FindInputs(SessionInputs).Where(input => input.IsEnabled))
        {
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        return !UpdateConfigurationErrors();
    }

    private bool UpdateConfigurationErrors()
    {
        bool hasErrors = FindInputs(SessionInputs).Any(input => input.IsEnabled && Validation.GetHasError(input));
        (DataContext as MainViewModel)?.SetConfigurationErrors(hasErrors);
        return hasErrors;
    }

    private static IEnumerable<TextBox> FindInputs(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is TextBox input)
            {
                yield return input;
            }
            else if (child is DependencyObject element)
            {
                foreach (TextBox descendant in FindInputs(element))
                {
                    yield return descendant;
                }
            }
        }
    }
}
