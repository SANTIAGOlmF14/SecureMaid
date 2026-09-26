using System.Text.Json.Serialization;

namespace SecureMaid.Models;

public enum ItemKind { Document, Image, Video, Folder, Other }

/// <summary>Un archivo cifrado y guardado dentro de la app (Caja Fuerte).</summary>
public class VaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalName { get; set; } = "";
    public string EncryptedFileName { get; set; } = ""; // nombre en disco dentro de la carpeta vault
    public ItemKind Kind { get; set; } = ItemKind.Other;
    public long SizeBytes { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.Now;

    /// <summary>Id del VaultItem (carpeta) que lo contiene, o null si esta en la raiz de la caja fuerte.</summary>
    public string? ParentFolderId { get; set; }
}

public enum HideMode
{
    /// <summary>Se mueve a un almacen controlado por la app. Desaparece por completo.</summary>
    MovedToStorage,
    /// <summary>Se queda en su sitio, solo se le ponen los atributos Oculto+Sistema.</summary>
    AttributesOnly,
    /// <summary>Ocultamiento parcial: se queda en su sitio (no se mueve ni se marca
    /// Oculto), pero se le quita el nombre visible y, si es carpeta, tambien el
    /// icono. Sigue estando ahi y se puede abrir con normalidad.</summary>
    Invisible
}

/// <summary>Una carpeta o archivo oculto (modo "invisible", no cifrado, pero
/// removido de la vista/busqueda del sistema y protegido con contrasena
/// para poder restaurarlo).</summary>
public class HiddenItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; set; } = "";
    /// <summary>Ruta actual en disco cuando es distinta de OriginalPath: el destino
    /// dentro del almacen (MovedToStorage) o la misma carpeta pero con nombre
    /// invisible (Invisible). Null en AttributesOnly, que no mueve ni renombra nada.</summary>
    public string? StoragePath { get; set; }
    public HideMode Mode { get; set; } = HideMode.MovedToStorage;
    public bool IsFolder { get; set; }
    public DateTime DateHidden { get; set; } = DateTime.Now;

    // Textos legibles para la lista de la interfaz (no se guardan en el indice).
    [System.Text.Json.Serialization.JsonIgnore] public string TypeDisplay => IsFolder ? "Carpeta" : "Archivo";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ModeDisplay => Mode switch
    {
        HideMode.MovedToStorage => "Completo (movido)",
        HideMode.Invisible => "Parcial (sin nombre ni icono)",
        _ => "Solo atributos"
    };
    [System.Text.Json.Serialization.JsonIgnore] public string DateDisplay => DateHidden.ToString("dd/MM/yyyy hh:mm tt");
}

/// <summary>Proteccion opcional de un elemento camuflado.</summary>
public enum DisguiseProtection
{
    /// <summary>Solo camuflado (nombre/icono). Sin contrasena.</summary>
    None,
    /// <summary>Contenido cifrado con una clave protegida por la contrasena general de la app.</summary>
    MasterPassword,
    /// <summary>Contenido cifrado con una clave protegida por una contrasena propia de este elemento.</summary>
    CustomPassword
}

/// <summary>Una carpeta o archivo camuflado (nombre + icono distinto). Si tiene
/// proteccion, ademas su contenido esta cifrado y solo se puede restaurar con la
/// contrasena correspondiente.</summary>
public class DisguisedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CurrentPath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string IconSource { get; set; } = ""; // "shell32.dll,3" por ejemplo
    public DateTime DateDisguised { get; set; } = DateTime.Now;

    public bool IsFolder { get; set; } = true;
    public DisguiseProtection Protection { get; set; } = DisguiseProtection.None;

    /// <summary>Clave aleatoria de cifrado del contenido, cifrada (base64) con la
    /// contrasena elegida (general o propia). La contrasena en si nunca se guarda.</summary>
    public string? WrappedKey { get; set; }

    /// <summary>Solo para carpetas con contenido cifrado: nombre secuencial usado en disco
    /// ("1", "2", ...) -> ruta relativa original dentro de la carpeta (incluye subcarpetas).
    /// Permite que los archivos queden con nombres anonimos ("1.smx", "2.smx"...) en vez de
    /// conservar su nombre real, y aun asi poder devolverlos a su nombre y ubicacion exactos.
    /// Null o vacio en elementos camuflados antes de este cambio (se usa el comportamiento anterior).</summary>
    public Dictionary<string, string>? NameMap { get; set; }

    /// <summary>True mientras el elemento esta "visible temporalmente" (Descamuflar rapido):
    /// el contenido esta descifrado y con su nombre original en disco, pero sigue en el
    /// registro con su misma configuracion (nombre, icono, proteccion) para poder volver a
    /// camuflarlo con "Volver a camuflar" sin repetir el formulario.</summary>
    public bool IsRevealed { get; set; } = false;

    // Textos legibles para la lista de la interfaz (no se guardan en el indice).
    [JsonIgnore] public string TypeDisplay => IsFolder ? "Carpeta" : "Archivo";
    [JsonIgnore] public string ProtectionDisplay => Protection switch
    {
        DisguiseProtection.MasterPassword => "Contraseña general",
        DisguiseProtection.CustomPassword => "Contraseña propia",
        _ => "Sin contraseña"
    };
    [JsonIgnore] public string DateDisplay => DateDisguised.ToString("dd/MM/yyyy");
    [JsonIgnore] public string StatusDisplay => IsRevealed ? "Visible temporalmente" : "Camuflado";
}
