using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CodexInstaller.Desktop.ViewModels;
using CodexInstaller.Desktop.Views;

namespace CodexInstaller.Desktop;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;

        return param switch
        {
            WelcomePageViewModel => new WelcomePage(),
            InstallPageViewModel => new InstallPage(),
            CompletePageViewModel => new CompletePage(),
            _ => new TextBlock { Text = "Not Found: " + param.GetType().FullName }
        };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
