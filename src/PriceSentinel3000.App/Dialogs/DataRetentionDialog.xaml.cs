using System.ComponentModel;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.IO;
using Microsoft.Win32;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Dialogs;

public partial class DataRetentionDialog : Window
{
    public DataRetentionDialog()
    {
        InitializeComponent();
    }

    private void DownloadJobsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DataRetentionViewModel viewModel) return;
        SortDescription? primary = viewModel.VisibleJobs.SortDescriptions.Count > 0
            ? viewModel.VisibleJobs.SortDescriptions[0] : null;
        foreach (DataGridColumn column in DownloadJobsGrid.Columns)
            column.SortDirection = column.SortMemberPath == primary?.PropertyName ? primary?.Direction : null;
    }

    private void BrowseLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DataRetentionViewModel viewModel) return;
        var picker = new OpenFolderDialog
        {
            Title = "Choose a market data library folder",
            InitialDirectory = Directory.Exists(viewModel.LibraryRootPath) ? viewModel.LibraryRootPath : "",
        };
        if (picker.ShowDialog(this) == true)
            viewModel.LibraryRootPath = picker.FolderName;
    }

    private async void ImportListFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DataRetentionViewModel { CanEditPlan: true } viewModel) return;
        var picker = new OpenFileDialog { Filter = "Download lists (*.json)|*.json" };
        if (picker.ShowDialog(this) == true)
            await viewModel.ExecuteAsync(async () =>
            {
                if (new FileInfo(picker.FileName).Length > 2_000_000) throw new ArgumentException("List import exceeds 2 MB.");
                await viewModel.ImportListsAsync(await File.ReadAllTextAsync(picker.FileName));
            });
    }

    private async void ExportLists_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DataRetentionViewModel { CanEditPlan: true } viewModel) return;
        var picker = new SaveFileDialog { Filter = "Download lists (*.json)|*.json", FileName = "download-lists.json" };
        if (picker.ShowDialog(this) == true)
            await viewModel.ExecuteAsync(() => File.WriteAllTextAsync(picker.FileName, viewModel.ExportLists()));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key is Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
