using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SecureMaid.Controls;
using SecureMaid.Helpers;
using SecureMaid.Managers;

namespace SecureMaid.Views;

public partial class LoginView : UserControl
{
    public event EventHandler? Unlocked;

    // Contrasena vigente recuperada al responder correctamente la pregunta de
    // seguridad (paso 1), lista para usarse como "contrasena anterior" al
    // restablecer (paso 2). Nunca se guarda en ningun lado, solo en memoria
    // mientras dura este flujo.
    private string? _recoveredPassword;

    public LoginView()
    {
        InitializeComponent();
        var bmp = Managers.LogoManager.LoadCurrentLogoBitmap(200);
        if (bmp != null) LogoImage.Source = bmp;
        Loaded += (_, __) =>
        {
            PasswordBox1.FocusInput();
            ForgotPasswordLink.Visibility = PasswordManager.HasSecurityQuestion()
                ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => TryUnlock();

    private void OnPasswordKeyDown(object? sender, EventArgs e) => TryUnlock();

    private void TryUnlock()
    {
        if (PasswordManager.VerifyMasterPassword(PasswordBox1.Password))
        {
            Session.CurrentPassword = PasswordBox1.Password;
            Unlocked?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ErrorText.Visibility = Visibility.Visible;
            PasswordBox1.Clear();
        }
    }

    // ---------------- Recuperar contraseña olvidada ----------------

    private void OnForgotPasswordClick(object sender, RoutedEventArgs e)
    {
        _recoveredPassword = null;
        LoginPanel.Visibility = Visibility.Collapsed;
        RecoveryPanel.Visibility = Visibility.Visible;
        RecoveryStepAnswer.Visibility = Visibility.Visible;
        RecoveryStepNewPassword.Visibility = Visibility.Collapsed;
        RecoveryQuestionText.Text = PasswordManager.Settings.SecurityQuestion;
        RecoveryAnswerBox.Clear();
        RecoveryError.Visibility = Visibility.Collapsed;
        RecoveryAnswerBox.Focus();
    }

    private void OnRecoveryAnswerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnVerifyAnswer(sender, e);
    }

    private void OnVerifyAnswer(object sender, RoutedEventArgs e)
    {
        string? recovered = PasswordManager.TryRecoverPassword(RecoveryAnswerBox.Text);
        if (recovered == null)
        {
            RecoveryError.Text = "Esa respuesta no es correcta.";
            RecoveryError.Visibility = Visibility.Visible;
            return;
        }

        _recoveredPassword = recovered;
        RecoveryError.Visibility = Visibility.Collapsed;
        RecoveryStepAnswer.Visibility = Visibility.Collapsed;
        RecoveryStepNewPassword.Visibility = Visibility.Visible;
        NewPasswordBox.Clear();
        NewPasswordBox2.Clear();
        RecoveryError2.Visibility = Visibility.Collapsed;
        NewPasswordBox.FocusInput();
    }

    private void OnResetPassword(object sender, RoutedEventArgs e)
    {
        if (_recoveredPassword == null) return; // no deberia poder llegar aqui sin pasar el paso 1

        string p1 = NewPasswordBox.Password;
        string p2 = NewPasswordBox2.Password;

        if (p1.Length < 6)
        {
            ShowRecoveryError2("La nueva contraseña debe tener al menos 6 caracteres.");
            return;
        }
        if (p1 != p2)
        {
            ShowRecoveryError2("Las contraseñas no coinciden.");
            return;
        }

        // Reutiliza la misma respuesta ya verificada en el paso 1 para que el
        // "recovery blob" quede actualizado con la contraseña nueva.
        var result = PasswordChangeService.ChangePassword(_recoveredPassword, p1, RecoveryAnswerBox.Text);
        if (!result.Success)
        {
            ShowRecoveryError2(result.Error ?? "No se pudo restablecer la contraseña.");
            return;
        }

        _recoveredPassword = null;
        AppMessageBox.Show("Contraseña restablecida. Ya puedes iniciar sesión con tu nueva contraseña.",
            "SecureMaid", MessageBoxButton.OK, MessageBoxImage.Information);
        OnCancelRecovery(sender, e);
    }

    private void ShowRecoveryError2(string msg)
    {
        RecoveryError2.Text = msg;
        RecoveryError2.Visibility = Visibility.Visible;
    }

    private void OnCancelRecovery(object sender, RoutedEventArgs e)
    {
        _recoveredPassword = null;
        RecoveryPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        PasswordBox1.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
        PasswordBox1.FocusInput();
    }

    private void OnPanelScroll(object sender, MouseWheelEventArgs e) => ScrollBehavior.RedirectToScrollViewer(sender, e);
}
