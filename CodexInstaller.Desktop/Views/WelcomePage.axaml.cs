using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CodexInstaller.Desktop.ViewModels;

namespace CodexInstaller.Desktop.Views;

public partial class WelcomePage : UserControl
{
    public WelcomePage()
    {
        InitializeComponent();
    }

    private async void BrowseInstallDir_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WelcomePageViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = "选择安装目录",
            AllowMultiple = false
        };

        if (!string.IsNullOrWhiteSpace(viewModel.InstallDir))
        {
            options.SuggestedStartLocation =
                await topLevel.StorageProvider.TryGetFolderFromPathAsync(viewModel.InstallDir);
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0)
        {
            viewModel.InstallDir = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        }
    }
}
