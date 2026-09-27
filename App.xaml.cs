using System.Threading.Tasks;
using System.Windows;
using SecureMaid.Controls;
using SecureMaid.Helpers;
using Velopack;

namespace SecureMaid;

public partial class App : Application
{
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
            var newVersion = await UpdateService.CheckForUpdatesAsync();
            if (newVersion == null) return; // ya esta en la ultima version

            var result = AppMessageBox.Show(
                $"Hay una nueva version disponible ({newVersion.TargetFullRelease.Version}). ¿Actualizar ahora?",
                "Actualizacion disponible", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return; // el usuario dijo que no: sigue con la version actual

            await UpdateService.DownloadAndApplyAsync(newVersion); // descarga, cierra la app, instala, y la reabre sola
        }
        catch
        {
            // Sin internet, o el repo/release todavia no existe: no molestamos al usuario.
            // (El boton "Buscar actualizaciones" de Ajustes sirve para diagnosticar esto a mano.)
        }
    }
}
