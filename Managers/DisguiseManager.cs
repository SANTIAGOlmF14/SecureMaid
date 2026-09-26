using System.IO;
using System.Runtime.InteropServices;

namespace SecureMaid.Managers;

/// <summary>
/// Camufla una carpeta: le cambia el nombre visible y el icono usando el
/// mecanismo nativo de Windows (desktop.ini + atributo System en la carpeta).
/// No requiere archivos .ico externos: se puede apuntar a iconos ya incluidos
/// en Windows (shell32.dll, imageres.dll) por indice, o a un .ico propio si
/// el usuario lo prefiere.
/// </summary>
public static class DisguiseManager
{
    // Iconos "neutros" listos para usar, sin levantar sospecha.
    // Formato: (nombre visible para el usuario, "archivo,indice")
    public static readonly (string Label, string IconSource)[] SuggestedIcons = new[]
    {
        ("Carpeta de sistema",        @"%SystemRoot%\System32\shell32.dll,4"),
        ("Controladores",             @"%SystemRoot%\System32\shell32.dll,1"),
        ("Documentos",                @"%SystemRoot%\System32\imageres.dll,2"),
        ("Copia de seguridad",        @"%SystemRoot%\System32\imageres.dll,105"),
        ("Herramientas de Windows",   @"%SystemRoot%\System32\shell32.dll,316"),
        ("Configuracion",             @"%SystemRoot%\System32\imageres.dll,109"),
        ("Red / Recursos compartidos",@"%SystemRoot%\System32\shell32.dll,17"),
        ("Papelera (carpeta vacia)",  @"%SystemRoot%\System32\imageres.dll,54"),
    };

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const int SHCNF_PATH = 0x0001;

    /// <summary>Aplica nombre + icono personalizados a una carpeta.</summary>
    public static string Disguise(string folderPath, string newDisplayName, string iconSource)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(folderPath);

        string parent = Path.GetDirectoryName(folderPath)!;
        string newPath = Path.Combine(parent, newDisplayName);

        if (!string.Equals(folderPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(folderPath, newPath);
        }

        string iniPath = Path.Combine(newPath, "desktop.ini");
        string iniContent =
            "[.ShellClassInfo]\r\n" +
            $"IconResource={iconSource}\r\n" +
            "[ViewState]\r\n" +
            "Mode=\r\n" +
            "Vid=\r\n" +
            "FolderType=Generic\r\n";

        File.WriteAllText(iniPath, iniContent);

        // El desktop.ini debe estar oculto, y la carpeta marcada System para
        // que el Explorador respete el icono/nombre personalizados.
        File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(newPath, File.GetAttributes(newPath) | FileAttributes.System);

        SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATH, Marshal.StringToHGlobalUni(newPath), IntPtr.Zero);

        return newPath;
    }

    /// <summary>Quita el camuflaje: borra desktop.ini y el atributo System.</summary>
    public static void RemoveDisguise(string folderPath, string? restoreOriginalName = null)
    {
        string iniPath = Path.Combine(folderPath, "desktop.ini");
        if (File.Exists(iniPath))
        {
            File.SetAttributes(iniPath, FileAttributes.Normal);
            File.Delete(iniPath);
        }

        var attrs = File.GetAttributes(folderPath);
        File.SetAttributes(folderPath, attrs & ~FileAttributes.System);

        string finalPath = folderPath;
        if (!string.IsNullOrWhiteSpace(restoreOriginalName))
        {
            string parent = Path.GetDirectoryName(folderPath)!;
            string original = Path.Combine(parent, restoreOriginalName);
            if (!string.Equals(folderPath, original, StringComparison.OrdinalIgnoreCase))
                Directory.Move(folderPath, original);
            finalPath = original;
        }

        // Igual que al camuflar: avisa al Explorador para que refresque el icono.
        IntPtr pathPtr = Marshal.StringToHGlobalUni(finalPath);
        try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATH, pathPtr, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(pathPtr); }
    }
}
