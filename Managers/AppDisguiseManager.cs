using System.IO;
using System.Runtime.InteropServices;

namespace SecureMaid.Managers;

/// <summary>
/// Camuflar la app en si tiene dos partes:
///  1) Como se ve "desde fuera": el nombre y el icono del acceso directo
///     (escritorio / menu inicio), y el titulo/icono de la ventana y la
///     barra de tareas mientras corre. Nada de esto requiere tecnicas de
///     evasion, es simple personalizacion de shortcuts y de la ventana WPF.
///  2) El "modo senuelo": la ventana principal, en vez de mostrar el
///     dashboard real al abrir, muestra algo inocuo (ej. una calculadora
///     funcional). Escribiendo la contrasena maestra en un lugar especifico
///     (ver DecoyCalculatorView) se revela la app real. Esto se controla
///     enteramente en AppSettings.DecoyModeEnabled, ver Helpers/PasswordManager.
/// </summary>
public static class AppDisguiseManager
{
    /// <summary>Crea (o actualiza) un acceso directo .lnk con nombre e icono
    /// personalizados que apunta al ejecutable de la app.</summary>
    public static void CreateDisguisedShortcut(string shortcutFolder, string shortcutName,
        string exePath, string iconSource)
    {
        string lnkPath = Path.Combine(shortcutFolder, shortcutName.EndsWith(".lnk") ? shortcutName : shortcutName + ".lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("WScript.Shell no disponible.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.IconLocation = iconSource; // ej: "C:\Windows\System32\imageres.dll,109"
            shortcut.Description = shortcutName;
            shortcut.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>Renombra el propio ejecutable (a Windows no le importa el
    /// nombre del .exe). Requiere reiniciar la app despues del cambio, ya
    /// que el proceso en ejecucion tiene el archivo bloqueado; por eso se
    /// hace en el proximo inicio via un pequeno script de renombrado.</summary>
    public static void ScheduleExeRename(string currentExePath, string newExeName)
    {
        string dir = Path.GetDirectoryName(currentExePath)!;
        string newPath = Path.Combine(dir, newExeName.EndsWith(".exe") ? newExeName : newExeName + ".exe");
        string batPath = Path.Combine(Path.GetTempPath(), "sv_rename.bat");

        // Espera a que el proceso actual cierre, luego renombra.
        string script =
            "@echo off\r\n" +
            "timeout /t 1 /nobreak > NUL\r\n" +
            $":retry\r\n" +
            $"ren \"{currentExePath}\" \"{Path.GetFileName(newPath)}\" 2>NUL\r\n" +
            "if exist \"" + currentExePath + "\" (timeout /t 1 /nobreak > NUL & goto retry)\r\n" +
            "del \"%~f0\"\r\n";

        File.WriteAllText(batPath, script);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = batPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        });
    }
}
