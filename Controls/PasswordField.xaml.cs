using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SecureMaid.Controls;

/// <summary>
/// Campo de contraseña reutilizable. Por dentro usa un PasswordBox normal y,
/// si el usuario toca el ojo 👁, lo cambia por un TextBox "gemelo" que
/// muestra el texto tal cual (sincronizando el valor al cambiar de modo).
/// Se usa en Login, en la creación de la contraseña y en Ajustes para que el
/// comportamiento de "ver/ocultar" sea idéntico en toda la app.
/// </summary>
public partial class PasswordField : UserControl
{
    /// <summary>Se dispara cuando el usuario presiona Enter dentro del campo,
    /// para poder enviar formularios con el teclado igual que antes.</summary>
    public event EventHandler? EnterPressed;

    private bool _showingText;

    public PasswordField()
    {
        InitializeComponent();
    }

    public string Password
    {
        get => _showingText ? InnerTextBox.Text : InnerPasswordBox.Password;
        set
        {
            InnerPasswordBox.Password = value ?? string.Empty;
            InnerTextBox.Text = value ?? string.Empty;
        }
    }

    public void Clear() => Password = string.Empty;

    /// <summary>Reemplaza a Focus() para asegurarse de enfocar el control interno
    /// que esté visible en este momento (PasswordBox o su gemelo TextBox).</summary>
    public void FocusInput()
    {
        if (_showingText)
        {
            InnerTextBox.Focus();
            InnerTextBox.CaretIndex = InnerTextBox.Text.Length;
        }
        else
        {
            InnerPasswordBox.Focus();
        }
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_showingText)
        {
            InnerTextBox.Text = InnerPasswordBox.Password;
            InnerPasswordBox.Visibility = Visibility.Collapsed;
            InnerTextBox.Visibility = Visibility.Visible;
            InnerTextBox.CaretIndex = InnerTextBox.Text.Length;
            InnerTextBox.Focus();
            EyeIcon.Data = (Geometry)FindResource("IconEyeOff");
            ToggleButton.ToolTip = "Ocultar contraseña";
        }
        else
        {
            InnerPasswordBox.Password = InnerTextBox.Text;
            InnerTextBox.Visibility = Visibility.Collapsed;
            InnerPasswordBox.Visibility = Visibility.Visible;
            InnerPasswordBox.Focus();
            EyeIcon.Data = (Geometry)FindResource("IconEye");
            ToggleButton.ToolTip = "Mostrar contraseña";
        }
        _showingText = !_showingText;
    }

    private void OnInnerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) EnterPressed?.Invoke(this, EventArgs.Empty);
    }
}
