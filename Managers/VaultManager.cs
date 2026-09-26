using System.IO;
using System.Text.Json;
using SecureMaid.Helpers;
using SecureMaid.Models;

namespace SecureMaid.Managers;

/// <summary>
/// La caja fuerte: archivos cifrados con AES-256-GCM guardados dentro de la
/// carpeta de datos de la app. Ni el nombre original ni el contenido son
/// legibles sin la contrasena.
/// </summary>
public class VaultManager
{
    private readonly string _vaultFolder;
    private readonly string _indexPath;
    private List<VaultItem> _items = new();

    public IReadOnlyList<VaultItem> Items => _items;

    public VaultManager()
    {
        _vaultFolder = Path.Combine(PasswordManager.AppDataFolder, "vault");
        _indexPath = Path.Combine(_vaultFolder, "index.dat"); // el indice tambien va cifrado
        Directory.CreateDirectory(_vaultFolder);
    }

    /// <summary>Carga el indice de la caja fuerte descifrandolo con la contrasena maestra.
    /// El indice es un JSON pequeno: se cifra/descifra directo en memoria (nunca toca
    /// disco en claro), a diferencia de los archivos grandes de la caja fuerte.</summary>
    public bool LoadIndex(string password)
    {
        if (!File.Exists(_indexPath))
        {
            _items = new List<VaultItem>();
            return true;
        }

        byte[]? plain = CryptoHelper.DecryptBytes(File.ReadAllBytes(_indexPath), password);
        if (plain == null) return false;

        _items = JsonSerializer.Deserialize<List<VaultItem>>(plain) ?? new List<VaultItem>();
        return true;
    }

    /// <summary>Escribe el indice cifrado de forma atomica: primero a un archivo
    /// ".new" y solo al final lo intercambia por el definitivo con File.Move,
    /// asi nunca queda un indice truncado si algo interrumpe la escritura.</summary>
    private void SaveIndex(string password)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_items);
        byte[] cipher = CryptoHelper.EncryptBytes(plain, password);
        string staged = _indexPath + ".new";
        File.WriteAllBytes(staged, cipher);
        File.Move(staged, _indexPath, overwrite: true);
    }

    public static ItemKind GuessKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Directory.Exists(path)) return ItemKind.Folder;
        if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(ext)) return ItemKind.Image;
        if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv" }.Contains(ext)) return ItemKind.Video;
        if (new[] { ".pdf", ".doc", ".docx", ".txt", ".xls", ".xlsx", ".ppt", ".pptx" }.Contains(ext)) return ItemKind.Document;
        return ItemKind.Other;
    }

    /// <summary>Cifra el archivo, lo guarda en la caja fuerte y borra el original de forma segura.</summary>
    public VaultItem AddFile(string filePath, string password, bool secureDeleteOriginal = true, string? parentFolderId = null)
    {
        var item = new VaultItem
        {
            OriginalName = Path.GetFileName(filePath),
            EncryptedFileName = Guid.NewGuid().ToString("N") + ".enc",
            Kind = GuessKind(filePath),
            SizeBytes = new FileInfo(filePath).Length,
            ParentFolderId = parentFolderId
        };

        string dest = Path.Combine(_vaultFolder, item.EncryptedFileName);
        CryptoHelper.EncryptFile(filePath, dest, password);

        if (secureDeleteOriginal)
            CryptoHelper.SecureDelete(filePath);
        else
            File.Delete(filePath);

        _items.Add(item);
        SaveIndex(password);
        return item;
    }

    /// <summary>Crea una carpeta virtual dentro de la caja fuerte (no ocupa espacio en disco,
    /// solo organiza los items del indice cifrado).</summary>
    public VaultItem CreateFolder(string name, string password, string? parentFolderId = null)
    {
        var item = new VaultItem
        {
            OriginalName = name,
            EncryptedFileName = "", // las carpetas no tienen archivo cifrado propio
            Kind = ItemKind.Folder,
            SizeBytes = 0,
            ParentFolderId = parentFolderId
        };
        _items.Add(item);
        SaveIndex(password);
        return item;
    }

    /// <summary>Items directos (archivos y subcarpetas) dentro de una carpeta dada (null = raiz).</summary>
    public IEnumerable<VaultItem> GetChildren(string? parentFolderId) =>
        _items.Where(i => i.ParentFolderId == parentFolderId)
              .OrderByDescending(i => i.Kind == ItemKind.Folder)
              .ThenBy(i => i.OriginalName, StringComparer.OrdinalIgnoreCase);

    public VaultItem? FindById(string id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>Devuelve, recursivamente, todos los ARCHIVOS (no carpetas) contenidos
    /// dentro de una carpeta de la caja fuerte, junto con su ruta relativa dentro de
    /// esa carpeta (incluyendo subcarpetas), para poder recrear la misma estructura
    /// al extraerlos afuera.</summary>
    public List<(VaultItem File, string RelativePath)> GetAllDescendantFiles(VaultItem folder)
    {
        var result = new List<(VaultItem, string)>();
        CollectDescendantFiles(folder.Id, "", result);
        return result;
    }

    private void CollectDescendantFiles(string? folderId, string relativePrefix, List<(VaultItem, string)> result)
    {
        foreach (var child in GetChildren(folderId))
        {
            string relativePath = string.IsNullOrEmpty(relativePrefix)
                ? child.OriginalName
                : Path.Combine(relativePrefix, child.OriginalName);

            if (child.Kind == ItemKind.Folder)
                CollectDescendantFiles(child.Id, relativePath, result);
            else
                result.Add((child, relativePath));
        }
    }

    /// <summary>Borra una carpeta de la caja fuerte solo si (ya) no tiene contenido,
    /// bajando primero por sus subcarpetas vacias. Se usa despues de "extraer y quitar"
    /// una carpeta completa: una vez que todos sus archivos se movieron afuera, esto
    /// limpia la carpeta (y las subcarpetas que hayan quedado vacias) del indice.</summary>
    public void PruneEmptyFolder(VaultItem folder, string password)
    {
        foreach (var child in GetChildren(folder.Id).Where(c => c.Kind == ItemKind.Folder).ToList())
            PruneEmptyFolder(child, password);

        if (!GetChildren(folder.Id).Any())
            RemoveFile(folder, password);
    }

    /// <summary>Descifra un item de la caja fuerte hacia una ruta destino elegida por el usuario.</summary>
    public bool ExtractFile(VaultItem item, string password, string destinationPath)
    {
        string source = Path.Combine(_vaultFolder, item.EncryptedFileName);
        return CryptoHelper.DecryptFile(source, destinationPath, password);
    }

    /// <summary>Descifra un item directo a memoria, para vistas previas (imagen/video).</summary>
    public byte[]? DecryptToBytes(VaultItem item, string password)
    {
        string source = Path.Combine(_vaultFolder, item.EncryptedFileName);
        return CryptoHelper.DecryptToBytes(source, password);
    }

    /// <summary>Borra permanentemente un item de la caja fuerte (irreversible).
    /// Si es una carpeta, borra tambien todo su contenido recursivamente.</summary>
    public void RemoveFile(VaultItem item, string password)
    {
        RemoveItemInternal(item);
        SaveIndex(password);
    }

    /// <summary>Representa un cambio de contrasena de la caja fuerte ya "preparado":
    /// cada archivo fue descifrado con la contrasena vieja y vuelto a cifrar con la
    /// nueva en archivos STAGED junto a los originales (con extension ".new"), sin
    /// tocar ni un solo archivo real todavia. <see cref="Commit"/> intercambia cada
    /// staged por su original con File.Move (una operacion de archivo, no de
    /// criptografia: muy rapida y con muy baja probabilidad de fallar). Si nunca se
    /// llama a Commit, Dispose() borra los staged sin haber tocado nada real.</summary>
    public sealed class VaultReencryptTransaction : IDisposable
    {
        private readonly string _indexPath;
        private readonly string _stagedIndexPath;
        private readonly List<(string Original, string Staged)> _fileMoves;
        private bool _committed;
        private bool _disposed;

        internal VaultReencryptTransaction(string indexPath, string stagedIndexPath,
            List<(string Original, string Staged)> fileMoves)
        {
            _indexPath = indexPath;
            _stagedIndexPath = stagedIndexPath;
            _fileMoves = fileMoves;
        }

        /// <summary>Aplica el cambio: mueve cada archivo re-cifrado a su lugar y por
        /// ultimo reemplaza el indice. Si esto lanza a mitad de camino, algunos
        /// archivos ya quedaron con la contrasena nueva y otros no -- por eso el
        /// llamador (PasswordChangeService) solo debe guardar el nuevo hash maestro
        /// DESPUES de que el Commit de los tres subsistemas (caja fuerte, ocultos,
        /// camuflados) haya terminado sin errores.</summary>
        public void Commit()
        {
            if (_committed) return;
            foreach (var (original, staged) in _fileMoves)
                File.Move(staged, original, overwrite: true);
            File.Move(_stagedIndexPath, _indexPath, overwrite: true);
            _committed = true;
        }

        public void Dispose()
        {
            if (_disposed || _committed) return;
            _disposed = true;
            foreach (var (_, staged) in _fileMoves)
                TryDelete(staged);
            TryDelete(_stagedIndexPath);
        }
    }

    /// <summary>Fase 1 del cambio de contrasena maestra: descifra cada archivo de la
    /// caja fuerte con <paramref name="oldPassword"/> y lo vuelve a cifrar con
    /// <paramref name="newPassword"/> en archivos staged (".new"), SIN tocar ningun
    /// archivo real de la caja fuerte todavia. Si algo falla -- contrasena vieja
    /// incorrecta, un archivo cifrado danado, error de E/S -- se lanza una excepcion
    /// y la caja fuerte queda exactamente como estaba: no hay archivos a medio
    /// migrar. Llamar a <see cref="VaultReencryptTransaction.Commit"/> en el objeto
    /// devuelto para aplicar el cambio, una vez que TODOS los subsistemas (caja
    /// fuerte, ocultos, camuflados) hayan preparado el suyo con exito.</summary>
    public VaultReencryptTransaction PrepareReencrypt(string oldPassword, string newPassword)
    {
        if (!LoadIndex(oldPassword))
            throw new InvalidOperationException("La contraseña actual no coincide con la caja fuerte.");

        var fileMoves = new List<(string Original, string Staged)>();
        string stagedIndexPath = _indexPath + ".new";
        try
        {
            foreach (var item in _items)
            {
                if (item.Kind == ItemKind.Folder || string.IsNullOrEmpty(item.EncryptedFileName))
                    continue;

                string path = Path.Combine(_vaultFolder, item.EncryptedFileName);
                if (!File.Exists(path)) continue;

                string tmpPlain = CryptoHelper.CreatePrivateTempFile();
                string staged = path + ".new";
                try
                {
                    if (!CryptoHelper.DecryptFile(path, tmpPlain, oldPassword))
                        throw new InvalidOperationException(
                            $"No se pudo descifrar '{item.OriginalName}' con la contraseña actual.");

                    CryptoHelper.EncryptFile(tmpPlain, staged, newPassword);
                }
                finally
                {
                    CryptoHelper.SecureDelete(tmpPlain);
                }
                fileMoves.Add((path, staged));
            }

            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_items);
            File.WriteAllBytes(stagedIndexPath, CryptoHelper.EncryptBytes(plain, newPassword));

            return new VaultReencryptTransaction(_indexPath, stagedIndexPath, fileMoves);
        }
        catch
        {
            foreach (var (_, staged) in fileMoves) TryDelete(staged);
            TryDelete(stagedIndexPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* limpieza de mejor esfuerzo */ }
    }

    private void RemoveItemInternal(VaultItem item)
    {
        if (item.Kind == ItemKind.Folder)
        {
            foreach (var child in GetChildren(item.Id).ToList())
                RemoveItemInternal(child);
        }
        else if (!string.IsNullOrEmpty(item.EncryptedFileName))
        {
            string source = Path.Combine(_vaultFolder, item.EncryptedFileName);
            CryptoHelper.SecureDelete(source);
        }
        _items.RemoveAll(i => i.Id == item.Id);
    }
}
