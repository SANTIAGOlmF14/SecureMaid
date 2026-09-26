using System.Windows;

namespace SecureMaid.Helpers;

/// <summary>
/// Controla el tema visual (oscuro/claro) de toda la app. El diccionario de
/// colores del tema activo siempre vive en la posicion 0 de
/// Application.Current.Resources.MergedDictionaries; los estilos (posicion 1)
/// usan DynamicResource para los colores, asi que se refrescan solos apenas
/// se reemplaza el diccionario de la posicion 0 — no hace falta reiniciar la
/// ventana ni la app para ver el cambio.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    public static string Current { get; private set; } = Dark;

    public static event EventHandler? ThemeChanged;

    /// <summary>Aplica el tema guardado en Ajustes. Se llama una vez al iniciar la app.</summary>
    public static void ApplyFromSettings() => Apply(PasswordManager.Settings.Theme, save: false);

    public static void Apply(string theme, bool save = true)
    {
        theme = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        Current = theme;

        var newDict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = newDict;
        else
            merged.Add(newDict);

        if (save)
        {
            PasswordManager.Settings.Theme = theme;
            PasswordManager.Save();
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Toggle() => Apply(Current == Dark ? Light : Dark);
}
