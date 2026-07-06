using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexInstaller.Desktop.ViewModels;

public partial class CompletePageViewModel : ViewModelBase
{
    public event EventHandler? RestartRequested;

    [ObservableProperty]
    private string installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Codex");

    [RelayCommand]
    private void Launch()
    {
        var launcherExe = Path.Combine(InstallDir, "Launcher", "Codex 启动.exe");

        if (File.Exists(launcherExe))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = launcherExe,
                WorkingDirectory = Path.GetDirectoryName(launcherExe),
                UseShellExecute = true
            });
            Environment.Exit(0);
        }
    }

    [RelayCommand]
    private void Exit()
    {
        Environment.Exit(0);
    }

    [RelayCommand]
    private void Restart()
    {
        RestartRequested?.Invoke(this, EventArgs.Empty);
    }
}
