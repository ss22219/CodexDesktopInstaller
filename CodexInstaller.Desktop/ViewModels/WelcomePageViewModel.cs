using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexInstaller.Desktop.ViewModels;

public partial class WelcomePageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "CodexFreeLauncher");

    public event EventHandler<string>? StartInstall;

    [RelayCommand]
    private void Install()
    {
        StartInstall?.Invoke(this, InstallDir);
    }
}
