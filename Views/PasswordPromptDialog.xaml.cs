using System.Windows;
using System.Windows.Input;

namespace SecureMaid.Views;

/// <summary>Dialogo con el mismo estilo que InputDialog, pero para pedir una
/// contraseña (con el ojo para mostrarla/ocultarla, igual que en el resto de la app).</summary>
public partial class PasswordPromptDialog : Window
{
    public string Password => PasswordInput.Password;

    public PasswordPromptDialog(string title, string prompt, string? errorMessage = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;

        if (!string.IsNullOrEmpty(errorMessage))
        {
            ErrorText.Text = errorMessage;
            ErrorText.Visibility = Visibility.Visible;
        }

        Loaded += (_, __) => PasswordInput.FocusInput();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnEnterPressed(object? sender, EventArgs e) => DialogResult = true;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    /// <summary>Muestra el dialogo y devuelve la contraseña escrita, o null si se cancelo o quedo vacia.</summary>
    public static string? Show(Window owner, string title, string prompt, string? errorMessage = null)
    {
        var dlg = new PasswordPromptDialog(title, prompt, errorMessage) { Owner = owner };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Password))
            return dlg.Password;
        return null;
    }
}
