using System.ComponentModel;
using System.IO;
using System.Windows;
using SecureMaid.Controls;
using SecureMaid.Helpers;

namespace SecureMaid.Views;

/// <summary>Reproduce un video de la caja fuerte. MediaElement (Media Foundation)
/// necesita un archivo en disco, asi que se descifra a un archivo temporal
/// privado dentro de la carpeta de datos de la app, y se borra de forma
/// segura en cuanto se cierra la ventana.</summary>
public partial class VideoPreviewWindow : Window
{
    private readonly string _tempPath;

    public VideoPreviewWindow(string displayName, string sourceEncryptedPath, string password, string extension)
    {
        InitializeComponent();
        FileNameText.Text = displayName;

        string tempDir = Path.Combine(PasswordManager.AppDataFolder, "preview_tmp");
        Directory.CreateDirectory(tempDir);
        _tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + extension);

        if (!CryptoHelper.DecryptFile(sourceEncryptedPath, _tempPath, password))
        {
            AppMessageBox.Show("No se pudo descifrar el video para previsualizarlo.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        Player.Source = new Uri(_tempPath, UriKind.Absolute);
        Loaded += (_, __) => Player.Play();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => Player.Play();
    private void OnPauseClick(object sender, RoutedEventArgs e) => Player.Pause();
    private void OnStopClick(object sender, RoutedEventArgs e) => Player.Stop();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            Player.Stop();
            Player.Close();
        }
        catch { /* ignorar */ }

        try
        {
            if (File.Exists(_tempPath))
                CryptoHelper.SecureDelete(_tempPath);
        }
        catch { /* si falla el borrado seguro, no bloquear el cierre */ }
    }
}
