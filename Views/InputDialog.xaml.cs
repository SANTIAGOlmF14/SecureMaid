using System.Windows;
using System.Windows.Input;

namespace SecureMaid.Views;

public partial class InputDialog : Window
{
    public string Value => ValueBox.Text;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        ValueBox.Text = defaultValue;
        Loaded += (_, __) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // El TextBox y la ventana tienen ambos un handler KeyDown: al presionar Enter
        // dentro del TextBox, el evento primero lo procesa el TextBox y despues burbujea
        // hasta la ventana, que volveria a intentar fijar DialogResult sobre una ventana
        // que ya se esta cerrando -> InvalidOperationException no capturada -> crash de
        // toda la app. Marcar e.Handled corta la burbuja despues del primer manejo.
        if (e.Handled) return;

        if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    /// <summary>Muestra el dialogo y devuelve el texto ingresado, o null si se cancelo o quedo vacio.</summary>
    public static string? Show(Window owner, string title, string prompt, string defaultValue = "")
    {
        var dlg = new InputDialog(title, prompt, defaultValue) { Owner = owner };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
            return dlg.Value.Trim();
        return null;
    }
}
