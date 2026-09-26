using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SecureMaid.Views;

/// <summary>Muestra una imagen de la caja fuerte descifrada directamente en
/// memoria (nunca se escribe el archivo en claro a disco).</summary>
public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(string displayName, byte[] imageBytes)
    {
        InitializeComponent();
        FileNameText.Text = displayName;

        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(imageBytes))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        PreviewImage.Source = bmp;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
