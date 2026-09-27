using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace SecureMaid.Helpers;

/// <summary>
/// Punto unico donde se arma la conexion con GitHub Releases via Velopack.
/// Lo usan tanto el chequeo automatico al abrir la app (App.xaml.cs) como el
/// boton "Buscar actualizaciones" de Ajustes, para no repetir la URL del
/// repo (y el bug de ".git" al final) en dos lugares distintos.
/// </summary>
public static class UpdateService
{
    // IMPORTANTE: sin ".git" al final. Velopack arma la URL de la API de
    // GitHub a partir de este path tal cual viene, y el ".git" rompe la
    // resolucion del nombre del repo (esto es lo que impedia que apareciera
    // el aviso de actualizacion en 1.0.0 -> 1.0.1).
    private const string GithubRepoUrl = "https://github.com/SANTIAGOlmF14/SecureMaid";

    /// <summary>Version instalada actualmente, tomada del ensamblado (coincide
    /// con &lt;Version&gt; del .csproj). Ej: "1.0.2".</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static UpdateManager CreateManager() =>
        new UpdateManager(new GithubSource(GithubRepoUrl, accessToken: null, prerelease: false));

    /// <summary>Busca actualizaciones en GitHub Releases. Devuelve null si ya
    /// se tiene la ultima version, o si la app no fue instalada con el
    /// instalador de Velopack (por ejemplo, corriendo el .exe suelto de la
    /// carpeta publish\ en vez de la copia instalada).</summary>
    public static async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        var mgr = CreateManager();
        if (!mgr.IsInstalled) return null;
        return await mgr.CheckForUpdatesAsync();
    }

    /// <summary>Descarga la actualizacion y reinicia la app ya actualizada.
    /// Si todo sale bien, el proceso actual se cierra solo: normalmente el
    /// codigo que llama a esto no sigue ejecutandose despues.</summary>
    public static async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        var mgr = CreateManager();
        await mgr.DownloadUpdatesAsync(info);
        mgr.ApplyUpdatesAndRestart(info);
    }
}
