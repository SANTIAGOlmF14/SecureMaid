using System.IO;

namespace SecureMaid.Managers;

/// <summary>
/// Maneja los logos disponibles para personalizar la apariencia de la app
/// (icono de la ventana / barra de tareas mientras corre). Son distintos de
/// los iconos de sistema que usa DisguiseManager para camuflar carpetas y
/// accesos directos: estos son imagenes propias (PNG) guardadas en la
/// carpeta "Logos" junto al ejecutable.
/// </summary>
public static class LogoManager
{
    public static string LogosFolder => Path.Combine(AppContext.BaseDirectory, "Logos");

    /// <summary>Lista los nombres de archivo (no la ruta completa) de los logos disponibles.</summary>
    public static string[] GetAvailableLogos()
    {
        if (!Directory.Exists(LogosFolder)) return Array.Empty<string>();
        return Directory.GetFiles(LogosFolder, "*.png")
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string GetLogoPath(string fileName) => Path.Combine(LogosFolder, fileName);

    public static string ResolveCurrentLogoPath()
    {
        string fileName = string.IsNullOrWhiteSpace(Helpers.PasswordManager.Settings.LogoFileName)
            ? "DefaultMaid.png" : Helpers.PasswordManager.Settings.LogoFileName;
        string path = GetLogoPath(fileName);
        if (File.Exists(path)) return path;

        // Si el logo elegido ya no existe, cae de vuelta al logo por defecto.
        string fallback = GetLogoPath("DefaultMaid.png");
        return File.Exists(fallback) ? fallback : "";
    }

    /// <summary>Carga el logo elegido actualmente como BitmapImage listo para usar
    /// en un control Image (congelado para poder usarse desde cualquier hilo).
    /// Devuelve null si no se pudo cargar ningun logo.</summary>
    public static System.Windows.Media.Imaging.BitmapImage? LoadCurrentLogoBitmap(int decodeWidth = 96)
    {
        try
        {
            string path = ResolveCurrentLogoPath();
            if (string.IsNullOrEmpty(path)) return null;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
