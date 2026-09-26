using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SecureMaid.Controls;
using SecureMaid.Helpers;
using SecureMaid.Managers;
using SecureMaid.Models;

namespace SecureMaid.Views;

/// <summary>
/// Visor unico de imagenes y videos de la Caja fuerte. Reemplaza a los
/// antiguos ImagePreviewWindow/VideoPreviewWindow: se abre maximizado, la
/// imagen siempre se ve completa (nunca mas grande que la pantalla), el
/// video tiene controles reales (play/pausa, barra de progreso arrastrable,
/// tiempo), y las flechas "anterior"/"siguiente" permiten recorrer todos los
/// items (fotos y videos mezclados) de la carpeta actual, dando la vuelta al
/// llegar al final.
/// </summary>
public partial class MediaPreviewWindow : Window
{
    private readonly VaultManager _vault;
    private readonly string _password;
    private readonly List<VaultItem> _items;
    private int _index;

    private string? _tempVideoPath;
    private readonly DispatcherTimer _positionTimer;
    private bool _userIsDragging;

    // Evita reproducir/limpiar un video que quedo "en vuelo" (descifrandose)
    // si el usuario ya paso a otro item antes de que terminara de cargar.
    private int _loadToken;

    // ---------------- Zoom / paneo de imagen ----------------
    private const double MinZoom = 0.5;
    private const double MaxZoom = 6.0;
    private const double ZoomStep = 0.18;
    private bool _isPanningImage;
    private Point _panStartMouse;
    private double _panStartX, _panStartY;

    public MediaPreviewWindow(VaultManager vault, string password, List<VaultItem> items, int startIndex)
    {
        InitializeComponent();
        _vault = vault;
        _password = password;
        _items = items;
        _index = startIndex;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += OnPositionTimerTick;

        // Con un solo item no tiene sentido mostrar flechas de navegacion.
        bool hasMultiple = _items.Count > 1;
        PrevButton.Visibility = hasMultiple ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = hasMultiple ? Visibility.Visible : Visibility.Collapsed;

        LoadCurrent();
    }

    // ---------------- Carga del item actual ----------------

    private async void LoadCurrent()
    {
        StopAndCleanupVideo();
        _loadToken++;
        int myToken = _loadToken;

        var item = _items[_index];
        FileNameText.Text = item.OriginalName;
        CounterText.Text = _items.Count > 1 ? $"{_index + 1} de {_items.Count}" : "";

        if (item.Kind == ItemKind.Image)
        {
            Player.Visibility = Visibility.Collapsed;
            VideoControlsBar.Visibility = Visibility.Collapsed;
            VideoLoadingText.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;
            ResetImageZoom();

            byte[]? bytes = _vault.DecryptToBytes(item, _password);
            if (bytes == null)
            {
                AppMessageBox.Show("No se pudo descifrar la imagen.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                PreviewImage.Source = null;
                return;
            }

            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            PreviewImage.Source = bmp;
        }
        else if (item.Kind == ItemKind.Video)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            Player.Visibility = Visibility.Visible;
            VideoControlsBar.Visibility = Visibility.Visible;
            VideoLoadingText.Visibility = Visibility.Visible;

            string vaultFolder = Path.Combine(PasswordManager.AppDataFolder, "vault");
            string source = Path.Combine(vaultFolder, item.EncryptedFileName);
            string ext = Path.GetExtension(item.OriginalName);

            string tempDir = Path.Combine(PasswordManager.AppDataFolder, "preview_tmp");
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ext);

            // Descifrar en un hilo de fondo: para videos grandes, hacerlo en el
            // hilo de UI era lo que congelaba la ventana ("tarda en cargar").
            bool ok = await Task.Run(() => CryptoHelper.DecryptFile(source, tempPath, _password));

            if (myToken != _loadToken)
            {
                // El usuario ya navego a otro item mientras esto se descifraba:
                // se descarta el resultado y se limpia el temporal huerfano.
                if (ok)
                {
                    try { CryptoHelper.SecureDelete(tempPath); } catch { /* no bloquear */ }
                }
                return;
            }

            VideoLoadingText.Visibility = Visibility.Collapsed;

            if (!ok)
            {
                AppMessageBox.Show("No se pudo descifrar el video para previsualizarlo.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _tempVideoPath = tempPath;
            SeekSlider.Value = 0;
            CurrentTimeText.Text = "0:00";
            DurationText.Text = "0:00";
            SetPlayingIcon(isPlaying: true);

            // Importante: con LoadedBehavior="Manual", el MediaElement no abre
            // el archivo hasta que se llama Play() (o Pause()). Por eso Play()
            // se llama de inmediato, en vez de esperar a MediaOpened: si se
            // espera, MediaOpened nunca llega a dispararse (candado circular).
            Player.Source = new Uri(_tempVideoPath, UriKind.Absolute);
            Player.Play();
            _positionTimer.Start();
        }
        else
        {
            // No deberia llegar aca: la lista de navegacion solo incluye imagenes/videos.
            PreviewImage.Visibility = Visibility.Visible;
            Player.Visibility = Visibility.Collapsed;
            VideoControlsBar.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
        }
    }

    private void StopAndCleanupVideo()
    {
        _positionTimer.Stop();
        try { Player.Stop(); Player.Close(); } catch { /* ignorar */ }
        Player.Source = null;

        if (_tempVideoPath != null && File.Exists(_tempVideoPath))
        {
            try { CryptoHelper.SecureDelete(_tempVideoPath); } catch { /* no bloquear */ }
        }
        _tempVideoPath = null;
    }

    // ---------------- Navegacion ----------------

    private void OnPrevClick(object sender, RoutedEventArgs e) => Navigate(-1);
    private void OnNextClick(object sender, RoutedEventArgs e) => Navigate(1);

    private void Navigate(int delta)
    {
        if (_items.Count <= 1) return;
        _index = (_index + delta + _items.Count) % _items.Count; // da la vuelta en ambos sentidos
        LoadCurrent();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left) { Navigate(-1); e.Handled = true; }
        else if (e.Key == Key.Right) { Navigate(1); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Space && Player.Visibility == Visibility.Visible) { TogglePlayPause(); e.Handled = true; }
    }

    // ---------------- Controles de video ----------------

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
        {
            SeekSlider.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
            DurationText.Text = FormatTime(Player.NaturalDuration.TimeSpan);
        }
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        Player.Pause();
        SetPlayingIcon(isPlaying: false);
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        bool isPlaying = PlayPauseIcon.Data == (Geometry)FindResource("IconPause");
        if (isPlaying) Player.Pause(); else Player.Play();
        SetPlayingIcon(!isPlaying);
    }

    private void SetPlayingIcon(bool isPlaying)
    {
        PlayPauseIcon.Data = (Geometry)FindResource(isPlaying ? "IconPause" : "IconPlay");
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        Player.Position = TimeSpan.Zero;
        SeekSlider.Value = 0;
        CurrentTimeText.Text = "0:00";
        SetPlayingIcon(isPlaying: false);
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => _userIsDragging = true;

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _userIsDragging = false;
        Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
    }

    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_userIsDragging) CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (_userIsDragging || Player.Source == null) return;
        SeekSlider.Value = Player.Position.TotalSeconds;
        CurrentTimeText.Text = FormatTime(Player.Position);
    }

    private static string FormatTime(TimeSpan t) =>
        t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---------------- Zoom / paneo de imagen ----------------
    // Zoom libre con la rueda del mouse (centrado), y paneo arrastrando con
    // el boton izquierdo una vez que la imagen esta ampliada. Doble clic
    // restablece el zoom. No afecta en nada al video ni a la navegacion.

    private void ResetImageZoom()
    {
        ImageScaleTransform.ScaleX = 1;
        ImageScaleTransform.ScaleY = 1;
        ImageTranslateTransform.X = 0;
        ImageTranslateTransform.Y = 0;
    }

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewImage.Visibility != Visibility.Visible || PreviewImage.Source == null) return;
        e.Handled = true;

        double factor = e.Delta > 0 ? 1 + ZoomStep : 1 - ZoomStep;
        double newScale = Math.Clamp(ImageScaleTransform.ScaleX * factor, MinZoom, MaxZoom);
        ImageScaleTransform.ScaleX = newScale;
        ImageScaleTransform.ScaleY = newScale;

        if (newScale <= 1.001)
        {
            // Al volver a 100% (o menos) no tiene sentido dejar el paneo desplazado.
            ImageTranslateTransform.X = 0;
            ImageTranslateTransform.Y = 0;
        }
        else
        {
            ClampImagePan();
        }
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Visibility != Visibility.Visible) return;

        if (e.ClickCount >= 2)
        {
            ResetImageZoom();
            e.Handled = true;
            return;
        }

        if (ImageScaleTransform.ScaleX <= 1.001) return;

        _isPanningImage = true;
        _panStartMouse = e.GetPosition(MediaStage);
        _panStartX = ImageTranslateTransform.X;
        _panStartY = ImageTranslateTransform.Y;
        PreviewImage.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanningImage) return;

        var pos = e.GetPosition(MediaStage);
        ImageTranslateTransform.X = _panStartX + (pos.X - _panStartMouse.X);
        ImageTranslateTransform.Y = _panStartY + (pos.Y - _panStartMouse.Y);
        ClampImagePan();
    }

    private void OnImageMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanningImage) return;

        _isPanningImage = false;
        PreviewImage.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
    }

    /// <summary>Evita que al hacer zoom/paneo la imagen se pueda arrastrar
    /// indefinidamente fuera de la vista, dejando la pantalla vacia.</summary>
    private void ClampImagePan()
    {
        double scale = ImageScaleTransform.ScaleX;
        double overflowX = Math.Max(0, (PreviewImage.ActualWidth * scale - MediaStage.ActualWidth) / 2);
        double overflowY = Math.Max(0, (PreviewImage.ActualHeight * scale - MediaStage.ActualHeight) / 2);

        ImageTranslateTransform.X = Math.Clamp(ImageTranslateTransform.X, -overflowX, overflowX);
        ImageTranslateTransform.Y = Math.Clamp(ImageTranslateTransform.Y, -overflowY, overflowY);
    }

    // ---------------- Cierre ----------------

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e) => StopAndCleanupVideo();
}
