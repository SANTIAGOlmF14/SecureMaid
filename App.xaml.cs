using System.Threading.Tasks;
using System.Windows;
using SecureMaid.Controls;
using SecureMaid.Helpers;
using Velopack;
using Velopack.Sources;

namespace SecureMaid;

public partial class App : Application
{
    // URL de tu repo de GitHub (la misma carpeta que ves en github.com/usuario/repo).
    // Si el repo es PRIVADO, tambien tenes que completar el accessToken mas abajo,
    // en CheckForUpdatesAsync, o esto nunca va a poder leer el feed de releases.
    private const string GithubRepoUrl = "https://github.com/SANTIAGOlmF14/SecureMaid.git";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Tiene que ser la PRIMERA linea de todas, antes que cualquier otra cosa
        // (incluso antes de base.OnStartup). Durante instalar/actualizar/desinstalar,
        // Velopack relanza el .exe con un argumento especial; Build().Run() detecta
        // eso, hace su trabajo, y sale del proceso ahi mismo -- sin esto, esas
        // operaciones internas ejecutarian por error toda tu app normal.
        VelopackApp.Build().Run();

        base.OnStartup(e);
        PasswordManager.Load();
        ThemeManager.ApplyFromSettings();

        // Chequeo de actualizaciones en segundo plano, sin bloquear el arranque.
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(
                GithubRepoUrl,
                accessToken: null, // repo privado -> poné acá tu Personal Access Token
                prerelease: false));

            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion == null) return; // ya esta en la ultima version

            var result = AppMessageBox.Show(
                $"Hay una nueva version disponible ({newVersion.TargetFullRelease.Version}). ¿Actualizar ahora?",
                "Actualizacion disponible", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return; // el usuario dijo que no: sigue con la version actual

            await mgr.DownloadUpdatesAsync(newVersion);
            mgr.ApplyUpdatesAndRestart(newVersion); // cierra la app, instala, y la reabre sola
        }
        catch
        {
            // Sin internet, o el repo/release todavia no existe: no molestamos al usuario.
        }
    }
}
