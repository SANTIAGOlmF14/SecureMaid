using System.Windows;
using SecureMaid.Helpers;

namespace SecureMaid;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        PasswordManager.Load();
        ThemeManager.ApplyFromSettings();
    }
}
