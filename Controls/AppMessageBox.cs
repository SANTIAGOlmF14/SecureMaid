using System.Linq;
using System.Windows;

namespace SecureMaid.Controls;

/// <summary>
/// Reemplazo drop-in de MessageBox.Show: misma firma y mismo tipo de retorno
/// (MessageBoxResult), pero dibuja un dialogo con el tema oscuro de la app en
/// vez del cuadro de alerta gris nativo de Windows.
/// </summary>
public static class AppMessageBox
{
    public static MessageBoxResult Show(string text) =>
        Show(text, "SecureMaid", MessageBoxButton.OK, MessageBoxImage.Information);

    public static MessageBoxResult Show(string text, string caption) =>
        Show(text, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) =>
        Show(text, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        var window = new AppMessageBoxWindow(text, caption, button, icon);

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        if (owner != null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.ShowDialog();
        return window.Result;
    }
}
