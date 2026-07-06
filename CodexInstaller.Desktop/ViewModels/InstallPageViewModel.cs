using CommunityToolkit.Mvvm.ComponentModel;
using CodexInstaller.Core;

namespace CodexInstaller.Desktop.ViewModels;

public partial class InstallPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _progressPercent;

    public string ProgressText => $"{ProgressPercent}%";

    [ObservableProperty]
    private string _statusText = "正在准备...";

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string? _errorMessage;

    partial void OnProgressPercentChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    public async Task<bool> StartInstallAsync(string installDir)
    {
        IsIndeterminate = false;
        ProgressPercent = 0;
        StatusText = "正在准备...";
        ErrorMessage = null;

        var bundleDir = Path.Combine(AppContext.BaseDirectory, "Bundle");
        var engine = new InstallEngine(installDir, bundleDir, progress =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ProgressPercent = progress.Percent;
                StatusText = progress.Status;
                ErrorMessage = progress.ErrorMessage;
            });
            return Task.CompletedTask;
        });

        try
        {
            await Task.Run(engine.InstallAsync);
            return true;
        }
        catch (Exception ex)
        {
            ProgressPercent = 0;
            StatusText = "安装失败";
            ErrorMessage = ex.Message;
            return false;
        }
    }
}
