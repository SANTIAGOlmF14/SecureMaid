using System.IO;
using System.Runtime.InteropServices;

namespace SecureMaid.Helpers;

/// <summary>
/// Deja una carpeta "presente pero en blanco": sin nombre visible (su nombre
/// real en disco es un caracter invisible) y sin icono (icono totalmente
/// transparente via desktop.ini). A diferencia de HideManager.HideByMoving,
/// la carpeta NO se mueve de su carpeta contenedora ni se marca Oculta: sigue
/// ahi mismo, se puede seguir haciendo doble clic para entrar con
/// normalidad, solo que no muestra nombre ni dibujo que llame la atencion.
///
/// Limitacion conocida: este truco de icono personalizado (desktop.ini) solo
/// lo respeta el Explorador para CARPETAS. Un archivo suelto no tiene forma
/// nativa de llevar un icono propio sin volverse un acceso directo, asi que
/// para archivos solo se aplica el nombre invisible (conserva su extension
/// para poder seguir abriendolo con el programa de siempre).
/// </summary>
public static class InvisibleIcon
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const int SHCNF_PATH = 0x0001;

    // PNG de 32x32 completamente transparente (0 bytes de "dibujo" real).
    // Va embebido en base64 para no depender de ningun archivo .ico externo
    // ni de System.Drawing: el proyecto se mantiene sin dependencias extra.
    private const string TransparentPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAGklEQVR42u3BAQEAAACCIP+vbkhAAQAAAO8GECAAAcm1w7EAAAAASUVORK5CYII=";

    /// <summary>Crea (si hace falta) el .ico transparente compartido y devuelve su ruta.
    /// Desde Windows Vista un .ico puede contener directamente un PNG con canal alfa,
    /// asi que basta con envolver el PNG en el encabezado minimo de un .ico.</summary>
    public static string EnsureTransparentIcon()
    {
        string folder = Path.Combine(PasswordManager.AppDataFolder, "assets");
        Directory.CreateDirectory(folder);
        string icoPath = Path.Combine(folder, "blank.ico");
        if (!File.Exists(icoPath))
        {
            byte[] png = Convert.FromBase64String(TransparentPngBase64);
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                // ICONDIR (6 bytes)
                bw.Write((ushort)0);   // reservado
                bw.Write((ushort)1);   // tipo = icono
                bw.Write((ushort)1);   // 1 imagen dentro

                // ICONDIRENTRY (16 bytes)
                bw.Write((byte)32);    // ancho
                bw.Write((byte)32);    // alto
                bw.Write((byte)0);     // sin paleta
                bw.Write((byte)0);     // reservado
                bw.Write((ushort)1);   // planes
                bw.Write((ushort)32);  // bits por pixel
                bw.Write((uint)png.Length);
                bw.Write((uint)22);    // offset de los datos = 6 + 16

                bw.Write(png);
            }
            File.WriteAllBytes(icoPath, ms.ToArray());
            File.SetAttributes(icoPath, FileAttributes.Hidden);
        }
        return icoPath;
    }

    /// <summary>Aplica el icono transparente a una carpeta (que ya debe estar
    /// renombrada con un nombre invisible; esto solo se encarga del icono).</summary>
    public static void Apply(string folderPath)
    {
        string icoPath = EnsureTransparentIcon();
        string iniPath = Path.Combine(folderPath, "desktop.ini");
        string iniContent =
            "[.ShellClassInfo]\r\n" +
            $"IconResource={icoPath},0\r\n" +
            "ConfirmFileOp=0\r\n" +
            "[ViewState]\r\n" +
            "Mode=\r\n" +
            "Vid=\r\n" +
            "FolderType=Generic\r\n";

        File.WriteAllText(iniPath, iniContent);
        File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(folderPath, File.GetAttributes(folderPath) | FileAttributes.System);

        Notify(folderPath);
    }

    /// <summary>Quita el desktop.ini y el atributo System de la carpeta. El nombre
    /// invisible NO se toca aqui; quien llama es quien la renombra de vuelta.</summary>
    public static void Remove(string folderPath)
    {
        string iniPath = Path.Combine(folderPath, "desktop.ini");
        if (File.Exists(iniPath))
        {
            File.SetAttributes(iniPath, FileAttributes.Normal);
            File.Delete(iniPath);
        }

        var attrs = File.GetAttributes(folderPath);
        File.SetAttributes(folderPath, attrs & ~FileAttributes.System);

        Notify(folderPath);
    }

    private static void Notify(string path)
    {
        IntPtr ptr = Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATH, ptr, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
