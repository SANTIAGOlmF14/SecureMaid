using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SecureMaid.Helpers;

namespace SecureMaid.Views;

public partial class SetupView : UserControl
{
    public event EventHandler? SetupCompleted;

    public SetupView()
    {
        InitializeComponent();
        var bmp = Managers.LogoManager.LoadCurrentLogoBitmap(200);
        if (bmp != null) LogoImage.Source = bmp;
    }

    private void OnPanelScroll(object sender, MouseWheelEventArgs e) => ScrollBehavior.RedirectToScrollViewer(sender, e);

    // El campo PasswordField ya trae su propio boton de mostrar/ocultar (👁),
    // asi que aqui solo se lee el valor final tal cual lo dejo el usuario.
    private string Password1 => PasswordBox1.Password;
    private string Password2 => PasswordBox2.Password;

    // ---------------- Pregunta de seguridad ----------------

    private void OnSecurityQuestionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomQuestionBox == null) return; // durante inicializacion de XAML
        bool isCustom = SecurityQuestionCombo.SelectedItem is ComboBoxItem item &&
                         string.Equals(item.Content?.ToString(), "Otra (la escribo yo)");
        CustomQuestionBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? GetSelectedQuestion()
    {
        if (SecurityQuestionCombo.SelectedItem is not ComboBoxItem item) return null;
        string label = item.Content?.ToString() ?? "";
        if (label == "Otra (la escribo yo)")
        {
            string custom = CustomQuestionBox.Text.Trim();
            return custom.Length == 0 ? null : custom;
        }
        return label;
    }

    // ---------------- Crear cuenta ----------------

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        string p1 = Password1;
        string p2 = Password2;

        if (p1.Length < 6)
        {
            ShowError("La contraseña debe tener al menos 6 caracteres.");
            return;
        }
        if (p1 != p2)
        {
            ShowError("Las contraseñas no coinciden.");
            return;
        }

        string? question = GetSelectedQuestion();
        if (question == null)
        {
            ShowError("Escribe tu pregunta de seguridad personalizada.");
            return;
        }
        if (SecurityAnswerBox.Text.Trim().Length < PasswordManager.MinSecurityAnswerLength)
        {
            ShowError($"La respuesta debe tener al menos {PasswordManager.MinSecurityAnswerLength} caracteres. " +
                      "Evita algo obvio o público: cualquiera que la sepa puede restablecer tu contraseña.");
            return;
        }

        PasswordManager.SetMasterPassword(p1);
        PasswordManager.SetSecurityQuestion(question, SecurityAnswerBox.Text, p1);

        // Antes de este cambio, la sesion nunca quedaba iniciada tras crear la
        // contrasena por primera vez, lo que disparaba un falso "sesion expirada"
        // apenas se abria el panel principal.
        Session.CurrentPassword = p1;

        SetupCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
