using System.Windows;
using System.Windows.Media.Imaging;
using SecureMaid.Helpers;
using SecureMaid.Managers;
using SecureMaid.Views;

namespace SecureMaid;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowState = WindowState.Maximized; // la app siempre abre en pantalla completa
        StateChanged += (_, __) => UpdateMaximizeGlyph();
        UpdateMaximizeGlyph();
        ApplyLogo();
        RouteInitialScreen();
    }

    /// <summary>Aplica el logo elegido en Ajustes como icono de la ventana/barra de tareas.
    /// Se llama de nuevo cada vez que el usuario cambia el logo en Ajustes.</summary>
    public void ApplyLogo()
    {
        try
        {
            string path = LogoManager.ResolveCurrentLogoPath();
            if (string.IsNullOrEmpty(path)) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            Icon = bmp;
            TitleBarIcon.Source = bmp;
        }
        catch
        {
            // si el logo no se pudo cargar, se conserva el icono anterior
        }
    }

    private void SetTitle(string title)
    {
        Title = title;
        TitleBarText.Text = title;
    }

    private void RouteInitialScreen()
    {
        if (!PasswordManager.HasMasterPassword())
        {
            ShowSetup();
        }
        else if (PasswordManager.Settings.DecoyModeEnabled)
        {
            ShowDecoy();
        }
        else
        {
            ShowLogin();
        }
    }

    public void ShowSetup()
    {
        var view = new SetupView();
        view.SetupCompleted += (_, __) => ShowDashboard();
        RootContent.Content = view;
        SetTitle("SecureMaid - Configuracion inicial");
    }

    public void ShowLogin()
    {
        var view = new LoginView();
        view.Unlocked += (_, __) => ShowDashboard();
        RootContent.Content = view;
        SetTitle(PasswordManager.Settings.DisguisedAppName);
    }

    public void ShowDecoy()
    {
        var view = new DecoyCalculatorView();
        view.Unlocked += (_, __) => ShowDashboard();
        RootContent.Content = view;
        SetTitle("Calculadora");
    }

    public void ShowDashboard()
    {
        var view = new DashboardView();
        view.LockRequested += (_, __) => RouteInitialScreen();
        RootContent.Content = view;
        SetTitle(PasswordManager.Settings.DisguisedAppName + " - Panel");
    }

    // ---------------- Barra de titulo personalizada ----------------

    private void UpdateMaximizeGlyph()
    {
        // E922 = maximizar, E923 = restaurar (Segoe MDL2 Assets)
        BtnMaximize.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        BtnMaximize.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
