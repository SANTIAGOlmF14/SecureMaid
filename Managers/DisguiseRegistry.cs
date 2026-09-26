using System.IO;
using System.Text.Json;
using SecureMaid.Helpers;
using SecureMaid.Models;

namespace SecureMaid.Managers;

public enum UndisguiseResult { Success, WrongPassword }

/// <summary>
/// Registro de carpetas/archivos camuflados. Se apoya en DisguiseManager para
/// lo visual (nombre + icono con desktop.ini) y agrega dos cosas:
///
///  1) Un indice cifrado con la contrasena general (mismo esquema que HideManager)
///     que recuerda el nombre original y la ruta de cada elemento, para poder
///     DESCAMUFLARLO y dejarlo exactamente como estaba.
///
///  2) Proteccion opcional con contrasena. Si el usuario la pide, el CONTENIDO se
///     cifra (AES-256-GCM) con una clave aleatoria propia del elemento. Esa clave
///     se guarda en el indice "envuelta" (cifrada) con la contrasena elegida:
///       - la contrasena general de la app, o
///       - una contrasena nueva e independiente solo para ese elemento.
///     Sin la contrasena correcta no hay forma de abrir el contenido: los archivos
///     quedan cifrados en disco. La contrasena en si nunca se guarda.
///
/// Carpetas: cada archivo de dentro se cifra a "nombre.ext.smx" (la estructura y
/// los nombres internos se conservan). Archivos: el contenido se cifra al
/// guardarse con el nuevo nombre visible.
///
/// Si cambia la contrasena general, PasswordChangeService llama a PrepareReencrypt,
/// que solo vuelve a "envolver" las claves de los elementos protegidos con ella:
/// no hay que re-cifrar el contenido.
/// </summary>
public class DisguiseRegistry
{
    private const string EncryptedExtension = ".smx";
    private const string TempExtension = ".smtmp";

    private static readonly EnumerationOptions IncludeHiddenAndSystem = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false
    };

    private readonly string _indexPath;
    private List<DisguisedItem> _items = new();

    public IReadOnlyList<DisguisedItem> Items => _items;

    /// <summary>Aviso no fatal de la ultima operacion (ej. no se pudo borrar un original
    /// porque estaba en uso). La interfaz lo muestra tras el mensaje de exito.</summary>
    public string? LastWarning { get; private set; }

    public DisguiseRegistry()
    {
        Directory.CreateDirectory(PasswordManager.AppDataFolder);
        _indexPath = Path.Combine(PasswordManager.AppDataFolder, "disguise_index.dat");
    }

    // ---------------- Indice cifrado ----------------

    /// <summary>El indice es un JSON pequeno: se cifra/descifra directo en memoria,
    /// nunca pasa por un archivo en claro en disco (ni siquiera temporal).</summary>
    public bool LoadIndex(string password)
    {
        if (!File.Exists(_indexPath)) { _items = new(); return true; }
        byte[]? plain = CryptoHelper.DecryptBytes(File.ReadAllBytes(_indexPath), password);
        if (plain == null) return false;
        _items = JsonSerializer.Deserialize<List<DisguisedItem>>(plain) ?? new();
        return true;
    }

    /// <summary>El indice guarda las claves de los elementos protegidos: si se perdiera,
    /// ese contenido no se podria recuperar. Por eso se escribe primero a un archivo
    /// ".new" y solo despues reemplaza al anterior con File.Move (nunca queda a medias).</summary>
    private void SaveIndex(string password)
    {
        byte[] cipher = CryptoHelper.EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(_items), password);
        string staged = _indexPath + ".new";
        try
        {
            File.WriteAllBytes(staged, cipher);
            File.Move(staged, _indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    /// <summary>Relee el indice desde disco antes de cada operacion, para no trabajar con
    /// una copia en memoria desactualizada (por ejemplo tras cambiar la contrasena general).</summary>
    private void RefreshFromDisk(string masterPassword)
    {
        if (!LoadIndex(masterPassword))
            throw new InvalidOperationException("No se pudo abrir el registro de camuflaje con la contraseña actual.");
    }

    /// <summary>Ver VaultManager.VaultReencryptTransaction / HideManager.ReencryptTransaction:
    /// mismo patron de dos fases. El indice nuevo (con las claves envueltas con la
    /// contrasena nueva) ya esta cifrado y escrito en un archivo ".new"; Commit()
    /// solo lo intercambia por el real.</summary>
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

    /// <summary>Fase 1 del cambio de contrasena maestra: vuelve a "envolver", con la
    /// contrasena nueva, la clave de cada elemento protegido con la contrasena general
    /// (los elementos con contrasena propia no se tocan: su proteccion es independiente),
    /// y prepara el indice resultante en un archivo ".new" SIN reemplazar el indice
    /// real todavia. Si algo falla -- contrasena vieja incorrecta, indice danado --
    /// se lanza una excepcion y el indice real queda intacto.</summary>
    public ReencryptTransaction PrepareReencrypt(string oldPassword, string newPassword)
    {
        if (!LoadIndex(oldPassword))
            throw new InvalidOperationException("La contraseña actual no coincide con el registro de camuflaje.");

        foreach (var item in _items.Where(i => i.Protection == DisguiseProtection.MasterPassword))
        {
            byte[]? key = Unwrap(item, oldPassword)
                ?? throw new InvalidOperationException($"No se pudo actualizar la protección de '{item.DisplayName}'.");
            item.WrappedKey = Wrap(key, newPassword);
            Array.Clear(key, 0, key.Length);
        }

        byte[] cipher = CryptoHelper.EncryptBytes(JsonSerializer.SerializeToUtf8Bytes(_items), newPassword);
        string staged = _indexPath + ".new";
        File.WriteAllBytes(staged, cipher);
        return new ReencryptTransaction(staged, _indexPath);
    }

    // ---------------- Utilidades ----------------

    private static string Wrap(byte[] key, string password) =>
        Convert.ToBase64String(CryptoHelper.EncryptBytes(key, password));

    private static byte[]? Unwrap(DisguisedItem item, string password)
    {
        if (string.IsNullOrEmpty(item.WrappedKey) || string.IsNullOrEmpty(password)) return null;
        try { return CryptoHelper.DecryptBytes(Convert.FromBase64String(item.WrappedKey), password); }
        catch (FormatException) { return null; }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public bool IsRegistered(string path) => _items.Any(i => PathsEqual(i.CurrentPath, path));

    /// <summary>Devuelve el elemento registrado en esa ruta (o null).</summary>
    public DisguisedItem? FindByPath(string path) => _items.FirstOrDefault(i => PathsEqual(i.CurrentPath, path));

    /// <summary>Devuelve un mensaje de error si el nombre no es valido en Windows, o null si esta bien.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Escribe el nuevo nombre visible.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "El nombre no puede contener ninguno de estos caracteres: \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' '))
            return "El nombre no puede terminar en punto ni en espacio.";
        return null;
    }

    // ---------------- Camuflar ----------------

    /// <param name="protection">Ninguna, contrasena general o contrasena propia.</param>
    /// <param name="customPassword">Solo si protection == CustomPassword.</param>
    /// <param name="masterPassword">Contrasena general de la sesion (abre/guarda el indice).</param>
    public DisguisedItem Camouflage(string path, string newName, string iconSource,
        DisguiseProtection protection, string? customPassword, string masterPassword)
    {
        LastWarning = null;
        RefreshFromDisk(masterPassword);

        path = Normalize(Path.GetFullPath(path));
        bool isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path))
            throw new FileNotFoundException("No se encontró el elemento a camuflar.", path);

        newName = (newName ?? "").Trim();
        string? nameError = ValidateName(newName);
        if (nameError != null) throw new ArgumentException(nameError);

        if (IsRegistered(path))
            throw new InvalidOperationException(
                "Este elemento ya está camuflado. Descamuflalo primero si quieres cambiarle el nombre o el icono.");

        string parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("No se puede camuflar una unidad completa.");
        string newPath = Path.Combine(parent, newName);
        bool samePath = PathsEqual(path, newPath);

        if (!samePath && (File.Exists(newPath) || Directory.Exists(newPath)))
            throw new IOException($"Ya existe un elemento llamado '{newName}' en esa ubicación.");
        if (samePath && !isFolder)
            throw new InvalidOperationException("Para un archivo, el nuevo nombre debe ser distinto al actual.");

        string? wrapPassword = protection switch
        {
            DisguiseProtection.MasterPassword => masterPassword,
            DisguiseProtection.CustomPassword => customPassword,
            _ => null
        };
        if (protection != DisguiseProtection.None && string.IsNullOrEmpty(wrapPassword))
            throw new ArgumentException("Falta la contraseña para proteger el elemento.");

        var item = new DisguisedItem
        {
            CurrentPath = newPath,
            OriginalName = Path.GetFileName(path),
            DisplayName = newName,
            IconSource = iconSource,
            IsFolder = isFolder,
            Protection = protection
        };

        byte[]? key = null;
        if (protection != DisguiseProtection.None)
        {
            key = CryptoHelper.GenerateRandomKey();
            item.WrappedKey = Wrap(key, wrapPassword!);
        }

        // Si se va a cifrar una carpeta, se decide DESDE YA con que nombres anonimos
        // ("1", "2"...) va a quedar cada archivo, y se guarda ese mapa junto con el resto
        // del registro (antes de tocar el disco), para poder devolver cada archivo a su
        // nombre y ubicacion real mas adelante.
        if (isFolder && key != null)
            item.NameMap = BuildNameMap(path);

        // Se registra ANTES de tocar el contenido: si el indice no se pudo guardar, no se
        // cifra nada (asi nunca hay contenido cifrado sin su clave guardada).
        _items.Add(item);
        try { SaveIndex(masterPassword); }
        catch { _items.Remove(item); throw; }

        bool contentTouched = false;
        try
        {
            if (isFolder)
            {
                if (key != null)
                {
                    contentTouched = true;
                    EncryptFolderContents(path, key, item.NameMap);
                }
                DisguiseManager.Disguise(path, newName, iconSource);
            }
            else if (key != null)
            {
                contentTouched = true;
                TransformFiles(new[] { (path, newPath) },
                    (src, dst) => { CryptoHelper.EncryptFileWithKey(src, dst, key!); return true; },
                    wipeSource: true);
            }
            else
            {
                File.Move(path, newPath);
            }
        }
        catch (Exception ex)
        {
            string? partialMessage = RollbackCamouflage(item, path, newPath, key, contentTouched, ex, masterPassword);
            if (partialMessage != null) throw new IOException(partialMessage, ex);
            throw;
        }
        finally
        {
            if (key != null) Array.Clear(key, 0, key.Length);
        }

        return item;
    }

    /// <summary>Deja todo como estaba si algo falla a la mitad de camuflar. Devuelve null si
    /// se pudo revertir por completo (se relanza el error original). Si algo quedo a medias
    /// devuelve un mensaje para el usuario y conserva el registro (con su clave), de modo
    /// que nada se pierda y pueda descamuflarlo despues.</summary>
    private string? RollbackCamouflage(DisguisedItem item, string path, string newPath,
        byte[]? key, bool contentTouched, Exception original, string masterPassword)
    {
        string? partialMessage = null;
        try
        {
            if (item.IsFolder)
            {
                // Si la carpeta ya se habia renombrado, primero se le quita el camuflaje visual.
                bool renamed = !PathsEqual(path, newPath) && !Directory.Exists(path) && Directory.Exists(newPath);
                if (renamed)
                {
                    try { DisguiseManager.RemoveDisguise(newPath, item.OriginalName); } catch { /* mejor esfuerzo */ }
                }
                string location = Directory.Exists(path) ? path : newPath;
                if (contentTouched && key != null)
                {
                    try { DecryptFolderContents(location, key, item.NameMap); }
                    catch
                    {
                        item.CurrentPath = location;
                        partialMessage =
                            $"No se pudo completar el camuflaje ({original.Message}) y tampoco revertir el cifrado. " +
                            "Tu carpeta sigue segura y aparece en 'Elementos camuflados': usa 'Descamuflar' para recuperarla.";
                    }
                }
            }
            else if (contentTouched && File.Exists(newPath))
            {
                // El archivo ya quedo cifrado con su nuevo nombre; solo fallo borrar el original.
                partialMessage =
                    $"El archivo se cifró y quedó como '{item.DisplayName}', pero no se pudo eliminar el original " +
                    $"({original.Message}). Bórralo manualmente: {path}";
            }
        }
        catch
        {
            partialMessage ??= $"No se pudo completar el camuflaje: {original.Message}";
            // Estado incierto: se conserva el registro por seguridad.
            return partialMessage;
        }

        if (partialMessage == null)
        {
            _items.Remove(item);
            try { SaveIndex(masterPassword); } catch { /* el registro huerfano es inofensivo */ }
        }
        return partialMessage;
    }

    // ---------------- Descamuflar ----------------

    /// <summary>Devuelve el elemento a su estado original (nombre, icono y contenido).
    /// Si esta protegido con contrasena propia, hay que pasarla en customPassword.
    /// Si esta protegido con la contrasena general se usa masterPassword (la de la sesion).</summary>
    public UndisguiseResult Uncamouflage(DisguisedItem item, string masterPassword, string? customPassword)
    {
        LastWarning = null;
        RefreshFromDisk(masterPassword);

        // La lista se recarga desde disco, asi que se busca por Id la version vigente.
        var current = _items.FirstOrDefault(i => i.Id == item.Id)
            ?? throw new InvalidOperationException("Este elemento ya no está en el registro de camuflaje.");

        string path = current.CurrentPath;
        if (current.IsFolder && !Directory.Exists(path))
            throw new DirectoryNotFoundException(path);
        if (!current.IsFolder && !File.Exists(path))
            throw new FileNotFoundException(path);

        string parent = Path.GetDirectoryName(path)!;
        string target = Path.Combine(parent, current.OriginalName);
        if (!PathsEqual(path, target) && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException(
                $"Ya existe un elemento llamado '{current.OriginalName}' en esa ubicación. " +
                "Renómbralo o muévelo y vuelve a intentarlo.");

        // 1) Contrasena: se comprueba ANTES de tocar nada.
        byte[]? key = null;
        if (current.Protection != DisguiseProtection.None)
        {
            string? pwd = current.Protection == DisguiseProtection.MasterPassword ? masterPassword : customPassword;
            key = Unwrap(current, pwd ?? "");
            if (key == null) return UndisguiseResult.WrongPassword;
        }

        try
        {
            // 2) Restaurar contenido y nombre/icono.
            if (current.IsFolder)
            {
                bool decrypted = false;
                if (key != null)
                {
                    decrypted = true;
                    DecryptFolderContents(path, key, current.NameMap);
                }
                try
                {
                    DisguiseManager.RemoveDisguise(path, current.OriginalName);
                }
                catch
                {
                    // Si no se pudo quitar el camuflaje visual, se vuelve a proteger el contenido
                    // para no dejarlo descifrado "a medias".
                    if (decrypted && key != null)
                    {
                        try { EncryptFolderContents(Directory.Exists(path) ? path : target, key, current.NameMap); } catch { }
                    }
                    throw;
                }
            }
            else if (key != null)
            {
                TransformFiles(new[] { (path, target) },
                    (src, dst) => CryptoHelper.DecryptFileWithKey(src, dst, key!),
                    wipeSource: false);
            }
            else
            {
                File.Move(path, target);
            }
        }
        finally
        {
            if (key != null) Array.Clear(key, 0, key.Length);
        }

        // 3) Fuera del registro.
        _items.RemoveAll(i => i.Id == current.Id);
        SaveIndex(masterPassword);
        return UndisguiseResult.Success;
    }

    /// <summary>"Descamuflar rapido": igual que Uncamouflage a nivel de disco (descifra el
    /// contenido y devuelve el nombre/icono original), pero NO saca el elemento del registro.
    /// Se marca IsRevealed = true y se conserva su configuracion (nombre visible, icono,
    /// proteccion, clave) para poder volver a camuflarlo despues con ReCamouflage sin repetir
    /// el formulario. Ideal para revisar el contenido y camuflarlo de nuevo enseguida.</summary>
    public UndisguiseResult RevealTemporarily(DisguisedItem item, string masterPassword, string? customPassword)
    {
        LastWarning = null;
        RefreshFromDisk(masterPassword);

        var current = _items.FirstOrDefault(i => i.Id == item.Id)
            ?? throw new InvalidOperationException("Este elemento ya no está en el registro de camuflaje.");

        if (current.IsRevealed)
            return UndisguiseResult.Success; // ya estaba visible: no hay nada que hacer.

        string path = current.CurrentPath;
        if (current.IsFolder && !Directory.Exists(path))
            throw new DirectoryNotFoundException(path);
        if (!current.IsFolder && !File.Exists(path))
            throw new FileNotFoundException(path);

        string parent = Path.GetDirectoryName(path)!;
        string target = Path.Combine(parent, current.OriginalName);
        if (!PathsEqual(path, target) && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException(
                $"Ya existe un elemento llamado '{current.OriginalName}' en esa ubicación. " +
                "Renómbralo o muévelo y vuelve a intentarlo.");

        byte[]? key = null;
        if (current.Protection != DisguiseProtection.None)
        {
            string? pwd = current.Protection == DisguiseProtection.MasterPassword ? masterPassword : customPassword;
            key = Unwrap(current, pwd ?? "");
            if (key == null) return UndisguiseResult.WrongPassword;
        }

        try
        {
            if (current.IsFolder)
            {
                bool decrypted = false;
                if (key != null)
                {
                    decrypted = true;
                    DecryptFolderContents(path, key, current.NameMap);
                }
                try
                {
                    DisguiseManager.RemoveDisguise(path, current.OriginalName);
                }
                catch
                {
                    if (decrypted && key != null)
                    {
                        try { EncryptFolderContents(Directory.Exists(path) ? path : target, key, current.NameMap); } catch { }
                    }
                    throw;
                }
            }
            else if (key != null)
            {
                TransformFiles(new[] { (path, target) },
                    (src, dst) => CryptoHelper.DecryptFileWithKey(src, dst, key!),
                    wipeSource: false);
            }
            else
            {
                File.Move(path, target);
            }
        }
        finally
        {
            if (key != null) Array.Clear(key, 0, key.Length);
        }

        current.CurrentPath = target;
        current.IsRevealed = true;
        SaveIndex(masterPassword);
        return UndisguiseResult.Success;
    }

    /// <summary>"Volver a camuflar": para un elemento dejado visible con RevealTemporarily,
    /// vuelve a aplicar EXACTAMENTE la misma configuracion (nombre visible, icono, tipo de
    /// proteccion) sin pedir nada nuevo salvo la contraseña necesaria para volver a envolver
    /// la clave (la general ya la tiene la sesion; la propia hay que pasarla porque nunca se
    /// guarda). Genera una clave de cifrado nueva y un NameMap nuevo: no hace falta que
    /// coincidan con los de la vez anterior.</summary>
    public DisguisedItem ReCamouflage(DisguisedItem item, string masterPassword, string? customPassword)
    {
        LastWarning = null;
        RefreshFromDisk(masterPassword);

        var current = _items.FirstOrDefault(i => i.Id == item.Id)
            ?? throw new InvalidOperationException("Este elemento ya no está en el registro de camuflaje.");

        if (!current.IsRevealed)
            throw new InvalidOperationException("Este elemento no está visible temporalmente (usa 'Descamuflar rápido' primero).");

        string path = current.CurrentPath;
        if (current.IsFolder && !Directory.Exists(path))
            throw new DirectoryNotFoundException(path);
        if (!current.IsFolder && !File.Exists(path))
            throw new FileNotFoundException(path);

        string parent = Path.GetDirectoryName(path)!;
        string newPath = Path.Combine(parent, current.DisplayName);
        if (!PathsEqual(path, newPath) && (File.Exists(newPath) || Directory.Exists(newPath)))
            throw new IOException($"Ya existe un elemento llamado '{current.DisplayName}' en esa ubicación.");

        string? wrapPassword = current.Protection switch
        {
            DisguiseProtection.MasterPassword => masterPassword,
            DisguiseProtection.CustomPassword => customPassword,
            _ => null
        };
        if (current.Protection != DisguiseProtection.None && string.IsNullOrEmpty(wrapPassword))
            throw new ArgumentException("Falta la contraseña para volver a proteger este elemento.");

        byte[]? key = null;
        if (current.Protection != DisguiseProtection.None)
        {
            key = CryptoHelper.GenerateRandomKey();
            current.WrappedKey = Wrap(key, wrapPassword!);
        }

        bool contentTouched = false;
        try
        {
            if (current.IsFolder)
            {
                if (key != null)
                {
                    current.NameMap = BuildNameMap(path);
                    contentTouched = true;
                    EncryptFolderContents(path, key, current.NameMap);
                }
                DisguiseManager.Disguise(path, current.DisplayName, current.IconSource);
            }
            else if (key != null)
            {
                contentTouched = true;
                TransformFiles(new[] { (path, newPath) },
                    (src, dst) => { CryptoHelper.EncryptFileWithKey(src, dst, key!); return true; },
                    wipeSource: true);
            }
            else
            {
                File.Move(path, newPath);
            }
        }
        catch
        {
            // Best-effort: si algo fallo a mitad de camino, se intenta descifrar lo que
            // alcanzo a cifrarse para no dejarlo a medias. El elemento sigue "revelado" en
            // el registro (nada se pierde) para poder reintentar.
            if (contentTouched && key != null)
            {
                try { DecryptFolderContents(Directory.Exists(path) ? path : newPath, key, current.NameMap); } catch { }
            }
            throw;
        }
        finally
        {
            if (key != null) Array.Clear(key, 0, key.Length);
        }

        current.CurrentPath = newPath;
        current.IsRevealed = false;
        SaveIndex(masterPassword);
        return current;
    }

    /// <summary>Quita un elemento SOLO del registro (no toca el disco). Se usa cuando el
    /// elemento ya no existe en su ruta (lo borraron o movieron por fuera de la app).</summary>
    public void Forget(DisguisedItem item, string masterPassword)
    {
        RefreshFromDisk(masterPassword);
        _items.RemoveAll(i => i.Id == item.Id);
        SaveIndex(masterPassword);
    }

    // ---------------- Cifrado de contenido ----------------

    private static IEnumerable<string> EnumerateFilesWithoutFollowingLinks(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", IncludeHiddenAndSystem))
            yield return file;

        foreach (var sub in Directory.EnumerateDirectories(directory, "*", IncludeHiddenAndSystem))
        {
            // No se entra en enlaces simbolicos / junctions: apuntan a contenido de otro lugar.
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in EnumerateFilesWithoutFollowingLinks(sub))
                yield return file;
        }
    }

    /// <summary>Decide con que nombre anonimo secuencial ("1", "2", ...) va a quedar cada
    /// archivo de la carpeta al cifrarla, y recuerda a que ruta relativa original corresponde
    /// cada uno. Las subcarpetas conservan su nombre real; solo los archivos se numeran.</summary>
    private static Dictionary<string, string> BuildNameMap(string root)
    {
        string ini = Path.Combine(root, "desktop.ini");
        var map = new Dictionary<string, string>();
        int i = 1;
        foreach (var file in EnumerateFilesWithoutFollowingLinks(root))
        {
            if (PathsEqual(file, ini)) continue;
            map[i.ToString()] = Path.GetRelativePath(root, file);
            i++;
        }
        return map;
    }

    private void EncryptFolderContents(string root, byte[] key, Dictionary<string, string>? nameMap)
    {
        // El desktop.ini de la raiz es el que crea/usa el camuflaje visual: no se cifra.
        string ini = Path.Combine(root, "desktop.ini");
        List<(string Src, string Dst)> pairs;

        if (nameMap != null && nameMap.Count > 0)
        {
            // Nombres anonimos: cada archivo queda como "<numero>.smx" en el mismo lugar
            // donde estaba (las subcarpetas no cambian de nombre, solo los archivos).
            pairs = nameMap
                .Select(kv =>
                {
                    string src = Path.Combine(root, kv.Value);
                    string dstDir = Path.GetDirectoryName(src) ?? root;
                    return (Src: src, Dst: Path.Combine(dstDir, kv.Key + EncryptedExtension));
                })
                .Where(p => !PathsEqual(p.Src, ini) && File.Exists(p.Src))
                .ToList();
        }
        else
        {
            // Comportamiento anterior: conserva el nombre real y solo agrega ".smx".
            pairs = EnumerateFilesWithoutFollowingLinks(root)
                .Where(f => !PathsEqual(f, ini))
                .Select(f => (Src: f, Dst: f + EncryptedExtension))
                .ToList();
        }
        if (pairs.Count == 0) return;

        TransformFiles(pairs,
            (src, dst) => { CryptoHelper.EncryptFileWithKey(src, dst, key); return true; },
            wipeSource: true);
    }

    private void DecryptFolderContents(string root, byte[] key, Dictionary<string, string>? nameMap)
    {
        // Solo se descifran los archivos .smx con la cabecera de la app; cualquier otro
        // archivo que el usuario haya dejado dentro se respeta tal cual.
        var encrypted = EnumerateFilesWithoutFollowingLinks(root)
            .Where(f => f.EndsWith(EncryptedExtension, StringComparison.OrdinalIgnoreCase)
                        && CryptoHelper.HasKeyedHeader(f))
            .ToList();
        if (encrypted.Count == 0) return;

        var pairs = new List<(string Src, string Dst)>();
        foreach (var file in encrypted)
        {
            string seq = Path.GetFileNameWithoutExtension(file); // "1" a partir de "1.smx"
            if (nameMap != null && nameMap.TryGetValue(seq, out var relativePath))
            {
                string dst = Path.Combine(root, relativePath);
                string? dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                pairs.Add((file, dst));
            }
            else
            {
                // Sin mapa (elemento camuflado antes de este cambio) o archivo fuera del
                // mapa: se respeta el comportamiento anterior, solo se quita ".smx".
                pairs.Add((file, file[..^EncryptedExtension.Length]));
            }
        }

        TransformFiles(pairs,
            (src, dst) => CryptoHelper.DecryptFileWithKey(src, dst, key),
            wipeSource: false);
    }

    /// <summary>Convierte una lista de archivos (cifrar o descifrar) en dos fases para no
    /// dejar el contenido a medias:
    ///   Fase 1: se genera cada resultado en un archivo temporal. Si algo falla (archivo en uso,
    ///           clave incorrecta, archivo danado) se borran los temporales y NO se cambia nada.
    ///   Fase 2: se reemplaza cada original por su resultado.</summary>
    private void TransformFiles(IReadOnlyList<(string Src, string Dst)> pairs,
        Func<string, string, bool> convert, bool wipeSource)
    {
        // Fase 0: comprobaciones baratas antes de escribir nada.
        foreach (var (src, dst) in pairs)
        {
            if (File.Exists(dst) || Directory.Exists(dst))
                throw new IOException($"Ya existe '{Path.GetFileName(dst)}' en '{Path.GetDirectoryName(dst)}'.");

            // Abrir en exclusiva detecta archivos que otro programa esta usando.
            using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        }

        // Fase 1
        var staged = new List<(string Src, string Tmp, string Dst)>();
        try
        {
            foreach (var (src, dst) in pairs)
            {
                string tmp = dst + TempExtension;
                staged.Add((src, tmp, dst)); // se anota antes para limpiarlo aunque falle a medias
                if (!convert(src, tmp))
                    throw new InvalidDataException(
                        $"No se pudo procesar '{Path.GetFileName(src)}': está dañado o fue modificado.");
            }
        }
        catch
        {
            foreach (var s in staged) TryDelete(s.Tmp);
            throw;
        }

        // Fase 2
        int notRemoved = 0;
        try
        {
            foreach (var (src, tmp, dst) in staged)
            {
                var info = new FileInfo(src);
                var attrs = info.Attributes;
                var created = info.CreationTimeUtc;
                var written = info.LastWriteTimeUtc;

                File.Move(tmp, dst);
                File.SetCreationTimeUtc(dst, created);
                File.SetLastWriteTimeUtc(dst, written);

                var keep = attrs & (FileAttributes.Hidden | FileAttributes.System |
                                    FileAttributes.ReadOnly | FileAttributes.Archive);
                File.SetAttributes(dst, keep == 0 ? FileAttributes.Normal : keep);

                try
                {
                    File.SetAttributes(src, FileAttributes.Normal);
                    if (wipeSource) CryptoHelper.SecureDelete(src); else File.Delete(src);
                }
                catch
                {
                    try { File.Delete(src); }
                    catch { notRemoved++; }
                }
            }
        }
        catch
        {
            foreach (var s in staged) TryDelete(s.Tmp);
            throw;
        }

        if (notRemoved > 0)
        {
            LastWarning = notRemoved == 1
                ? "No se pudo borrar 1 archivo original porque estaba en uso. Bórralo manualmente para que no quede sin cifrar."
                : $"No se pudieron borrar {notRemoved} archivos originales porque estaban en uso. Bórralos manualmente para que no queden sin cifrar.";
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* limpieza de mejor esfuerzo */ }
    }
}
