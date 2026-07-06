using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodexInstaller.Core;

namespace CodexInstaller.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private object _currentPage;

    public WelcomePageViewModel WelcomePage { get; }
    public InstallPageViewModel InstallPage { get; }
    public CompletePageViewModel CompletePage { get; }

    public MainWindowViewModel()
    {
        WelcomePage = new WelcomePageViewModel();
        InstallPage = new InstallPageViewModel();
        CompletePage = new CompletePageViewModel();

        _currentPage = WelcomePage;

        WelcomePage.StartInstall += OnStartInstall;
        CompletePage.RestartRequested += OnRestart;
    }

    private async void OnStartInstall(object? sender, string installDir)
    {
        CurrentPage = InstallPage;
        if (await InstallPage.StartInstallAsync(installDir))
        {
            CompletePage.InstallDir = installDir;
            CurrentPage = CompletePage;
        }
    }

    private void OnRestart(object? sender, EventArgs e)
    {
        CurrentPage = WelcomePage;
    }
}
