using System.IO;

namespace SecureMaid.Managers;

/// <summary>
/// El panel "Inicio" del Explorador de Windows (Reciente) se arma a partir de
/// accesos directos .lnk guardados en %APPDATA%\Microsoft\Windows\Recent, y de
/// los Jump Lists (%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations).
/// Esta clase elimina lo que apunte a las rutas que el usuario oculto, y
/// ofrece un "borrado total" para los Jump Lists (mas bruto, pero 100% efectivo
/// cuando se necesita limpiar todo rastro reciente de una).
/// </summary>
public static class RecentCleaner
{
    private static readonly string RecentFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    private static readonly string AutomaticDestinations = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Recent", "AutomaticDestinations");

    /// <summary>Borra los .lnk de Recientes que apunten (o cuyo nombre coincida)
    /// a la ruta indicada. Usa WScript.Shell via COM tardio para resolver el
    /// destino real de cada acceso directo, sin necesitar referencias extra.</summary>
    public static void RemoveReferencesTo(string targetPath)
    {
        if (!Directory.Exists(RecentFolder)) return;

        string targetName = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar));
        dynamic? shell = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
                shell = Activator.CreateInstance(shellType);

            foreach (var lnk in Directory.GetFiles(RecentFolder, "*.lnk"))
            {
                bool matches = false;
                try
                {
                    if (shell != null)
                    {
                        dynamic shortcut = shell.CreateShortcut(lnk);
                        string dest = shortcut.TargetPath as string ?? "";
                        if (dest.Equals(targetPath, StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(lnk).Contains(targetName, StringComparison.OrdinalIgnoreCase))
                        {
                            matches = true;
                        }
                    }
                }
                catch
                {
                    // si no se puede resolver el .lnk, cae al menos a comparar por nombre
                    if (Path.GetFileName(lnk).Contains(targetName, StringComparison.OrdinalIgnoreCase))
                        matches = true;
                }

                if (matches)
                {
                    try { File.Delete(lnk); } catch { /* en uso o sin permiso, se ignora */ }
                }
            }
        }
        finally
        {
            if (shell != null)
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>Borra TODOS los accesos de "Reciente" (blunt pero efectivo).</summary>
    public static void ClearAllRecentShortcuts()
    {
        if (!Directory.Exists(RecentFolder)) return;
        foreach (var f in Directory.GetFiles(RecentFolder, "*.lnk"))
        {
            try { File.Delete(f); } catch { }
        }
    }

    /// <summary>Borra los Jump Lists (listas de "abiertos recientemente" que
    /// aparecen al hacer clic derecho sobre un icono en la barra de tareas,
    /// y que tambien alimentan paneles de "Inicio" de apps como el Explorador
    /// o visores de fotos/videos). Es un borrado total, no selectivo.</summary>
    public static void ClearAllJumpLists()
    {
        if (!Directory.Exists(AutomaticDestinations)) return;
        foreach (var f in Directory.GetFiles(AutomaticDestinations))
        {
            try { File.Delete(f); } catch { }
        }
    }
}
