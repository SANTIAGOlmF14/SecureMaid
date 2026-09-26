using System.IO;
using System.Text.Json;
using SecureMaid.Helpers;
using SecureMaid.Models;

namespace SecureMaid.Managers;

/// <summary>
/// Oculta carpetas/archivos. El modo recomendado (MovedToStorage) los MUEVE
/// fuera de su ubicacion original hacia una carpeta controlada por la app.
/// Esto es lo que evita que aparezcan en listados tipo "todos los videos del
/// PC" de apps de galeria, en busquedas de Windows, o en Recientes/Inicio -
/// porque el archivo ya NO esta en la ruta original, asi que no hay nada que
/// esas apps puedan indexar ahi. Restaurar requiere la contrasena maestra.
/// </summary>
public class HideManager
{
    private readonly string _storageFolder;
    private readonly string _indexPath;
    private List<HiddenItem> _items = new();

    public IReadOnlyList<HiddenItem> Items => _items;

    public HideManager()
    {
        _storageFolder = Path.Combine(PasswordManager.AppDataFolder, "hidden_storage");
        _indexPath = Path.Combine(PasswordManager.AppDataFolder, "hidden_index.dat");
        Directory.CreateDirectory(_storageFolder);
    }

    /// <summary>El indice es un JSON pequeno: se cifra/descifra directo en memoria
    /// (nunca toca disco en claro, ni siquiera en un archivo temporal).</summary>
    public bool LoadIndex(string password)
    {
        if (!File.Exists(_indexPath)) { _items = new(); return true; }
        byte[]? plain = CryptoHelper.DecryptBytes(File.ReadAllBytes(_indexPath), password);
        if (plain == null) return false;
        _items = JsonSerializer.Deserialize<List<HiddenItem>>(plain) ?? new();
        return true;
    }

    /// <summary>Escritura atomica: primero a ".new", y solo al final se intercambia
    /// por el archivo real con File.Move (nunca queda un indice truncado a medias).</summary>
    private void SaveIndex(string password)
    {
        byte[] cipher = CryptoHelper.EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(_items), password);
        string staged = _indexPath + ".new";
        File.WriteAllBytes(staged, cipher);
        File.Move(staged, _indexPath, overwrite: true);
    }

    /// <summary>Representa un re-cifrado del indice de ocultos ya preparado (el JSON
    /// nuevo ya fue cifrado con la contrasena nueva en un archivo ".new"), pero sin
    /// haber reemplazado el indice real todavia. Ver PasswordChangeService: solo se
    /// hace Commit() de los tres subsistemas (caja fuerte, ocultos, camuflados) una
    /// vez que los tres se prepararon sin errores.</summary>
    public sealed class ReencryptTransaction : IDisposable
    {
        private readonly string _stagedPath;
        private readonly string _indexPath;
        private bool _committed;
        private bool _disposed;

        internal ReencryptTransaction(string stagedPath, string indexPath)
        {
            _stagedPath = stagedPath;
            _indexPath = indexPath;
        }

        public void Commit()
        {
            if (_committed) return;
            File.Move(_stagedPath, _indexPath, overwrite: true);
            _committed = true;
        }

        public void Dispose()
        {
            if (_disposed || _committed) return;
            _disposed = true;
            try { if (File.Exists(_stagedPath)) File.Delete(_stagedPath); } catch { /* mejor esfuerzo */ }
        }
    }

    /// <summary>Fase 1 del cambio de contrasena maestra para el indice de ocultos.
    /// Los archivos/carpetas ocultos en si NO estan cifrados con la contrasena
    /// (solo se mueven y se marcan Oculto/Sistema), asi que solo hay que preparar
    /// el nuevo indice cifrado; no hay que tocar los archivos ocultos en disco.</summary>
    public ReencryptTransaction PrepareReencrypt(string oldPassword, string newPassword)
    {
        if (!LoadIndex(oldPassword))
            throw new InvalidOperationException("La contraseña actual no coincide con el índice de ocultos.");

        byte[] cipher = CryptoHelper.EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(_items), newPassword);
        string staged = _indexPath + ".new";
        File.WriteAllBytes(staged, cipher);
        return new ReencryptTransaction(staged, _indexPath);
    }

    /// <summary>Oculta moviendo el archivo/carpeta a almacenamiento controlado
    /// por la app (recomendado: es lo que garantiza que no aparezca en
    /// galerias/busquedas/recientes, porque deja de existir en la ruta original).</summary>
    public HiddenItem HideByMoving(string path, string password)
    {
        bool isFolder = Directory.Exists(path);
        string newLocation = Path.Combine(_storageFolder, Guid.NewGuid().ToString("N"));

        if (isFolder)
            Directory.Move(path, newLocation);
        else
            File.Move(path, newLocation);

        // Ademas, por capa extra, se marca oculto+sistema en su nueva ubicacion
        SetHiddenAttributes(newLocation, isFolder);

        var item = new HiddenItem
        {
            OriginalPath = path,
            StoragePath = newLocation,
            Mode = HideMode.MovedToStorage,
            IsFolder = isFolder
        };
        _items.Add(item);
        SaveIndex(password);

        // Limpia cualquier rastro en "Recientes" que apunte a la ruta original
        RecentCleaner.RemoveReferencesTo(path);

        return item;
    }

    /// <summary>Ocultamiento parcial: NO mueve el archivo/carpeta de su sitio ni lo
    /// marca Oculto. Solo le quita el nombre visible (queda con un caracter
    /// invisible como nombre) y, si es carpeta, tambien el icono (queda
    /// transparente). Sigue estando ahi, se puede seguir abriendo con doble clic;
    /// simplemente no muestra nombre ni dibujo que llame la atencion.</summary>
    public HiddenItem HideInvisible(string path, string password)
    {
        bool isFolder = Directory.Exists(path);
        string parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("No se pudo determinar la carpeta contenedora.");
        string extension = isFolder ? "" : Path.GetExtension(path);

        string invisibleName = GenerateInvisibleName(parent, extension);
        string newPath = Path.Combine(parent, invisibleName);

        if (isFolder)
            Directory.Move(path, newPath);
        else
            File.Move(path, newPath);

        if (isFolder)
            InvisibleIcon.Apply(newPath);

        var item = new HiddenItem
        {
            OriginalPath = path,
            StoragePath = newPath,
            Mode = HideMode.Invisible,
            IsFolder = isFolder
        };
        _items.Add(item);
        SaveIndex(password);

        RecentCleaner.RemoveReferencesTo(path);

        return item;
    }

    /// <summary>Genera un nombre de archivo/carpeta visualmente en blanco (usando
    /// caracteres Unicode invisibles en vez de texto) y unico dentro de esa
    /// carpeta contenedora, para no chocar con otro elemento ya invisible ahi.</summary>
    private static string GenerateInvisibleName(string parentDir, string extension)
    {
        // U+00A0 (espacio de no separacion): Windows lo acepta como nombre valido
        // porque solo recorta espacios ASCII normales al final, no este.
        string extra = "";
        string candidate = "\u00A0" + extension;
        while (File.Exists(Path.Combine(parentDir, candidate)) || Directory.Exists(Path.Combine(parentDir, candidate)))
        {
            // Si ya hay otro elemento invisible ahi, se agrega otro caracter
            // invisible (U+2007) para diferenciarlos sin que se note nada.
            extra += "\u2007";
            candidate = "\u00A0" + extra + extension;
        }
        return candidate;
    }

    /// <summary>Abre el elemento en su ubicacion actual con el programa
    /// predeterminado, sin necesidad de restaurarlo primero. Funciona para
    /// cualquier modo: MovedToStorage y Invisible usan StoragePath; AttributesOnly
    /// se queda con OriginalPath porque nunca se movio.</summary>
    public void Open(HiddenItem item)
    {
        string path = item.StoragePath ?? item.OriginalPath;
        var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>Modo ligero: solo aplica atributos Hidden+System en el sitio.
    /// Mas rapido, pero un usuario que active "mostrar archivos protegidos del
    /// sistema" en el Explorador lo veria igual. Util si el usuario no quiere
    /// que la app mueva archivos de sitio.</summary>
    public HiddenItem HideInPlace(string path, string password)
    {
        bool isFolder = Directory.Exists(path);
        SetHiddenAttributes(path, isFolder);

        var item = new HiddenItem
        {
            OriginalPath = path,
            StoragePath = null,
            Mode = HideMode.AttributesOnly,
            IsFolder = isFolder
        };
        _items.Add(item);
        SaveIndex(password);
        RecentCleaner.RemoveReferencesTo(path);
        return item;
    }

    private static void SetHiddenAttributes(string path, bool isFolder)
    {
        var attrs = File.GetAttributes(path);
        File.SetAttributes(path, attrs | FileAttributes.Hidden | FileAttributes.System);
    }

    /// <summary>Restaura un item oculto a su ruta original (o a otra si el usuario lo pide).</summary>
    public bool Unhide(HiddenItem item, string password, string? restoreToPath = null)
    {
        string destination = restoreToPath ?? item.OriginalPath;

        if (item.Mode == HideMode.MovedToStorage && item.StoragePath != null)
        {
            // quita atributos antes de mover de vuelta
            var attrs = File.GetAttributes(item.StoragePath);
            File.SetAttributes(item.StoragePath, attrs & ~FileAttributes.Hidden & ~FileAttributes.System);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (item.IsFolder)
                Directory.Move(item.StoragePath, destination);
            else
                File.Move(item.StoragePath, destination);
        }
        else if (item.Mode == HideMode.Invisible && item.StoragePath != null)
        {
            // primero se le quita el icono transparente (si era carpeta) y luego
            // se renombra de vuelta a su nombre real; nunca se movio de carpeta
            // contenedora, asi que no hace falta recrear ninguna ruta.
            if (item.IsFolder)
                InvisibleIcon.Remove(item.StoragePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (item.IsFolder)
                Directory.Move(item.StoragePath, destination);
            else
                File.Move(item.StoragePath, destination);
        }
        else
        {
            var attrs = File.GetAttributes(item.OriginalPath);
            File.SetAttributes(item.OriginalPath, attrs & ~FileAttributes.Hidden & ~FileAttributes.System);
        }

        _items.RemoveAll(i => i.Id == item.Id);
        SaveIndex(password);
        return true;
    }
}
