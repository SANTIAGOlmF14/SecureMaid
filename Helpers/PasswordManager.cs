using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureMaid.Helpers;

public class AppSettings
{
    public string? MasterPasswordHash { get; set; } // formato: salt(base64):hash(base64)
    public bool DecoyModeEnabled { get; set; } = false;
    public string DisguisedAppName { get; set; } = "SecureMaid";
    public string ShellIconSource { get; set; } = ""; // ej: "C:\\Windows\\System32\\shell32.dll,3"
    public bool RequirePasswordOnEveryLaunch { get; set; } = true;
    public bool RequirePasswordOnlyForVault { get; set; } = false;
    public string LogoFileName { get; set; } = "DefaultMaid.png"; // archivo dentro de la carpeta Logos
    public string Theme { get; set; } = "Dark"; // "Dark" o "Light"

    // Pregunta de seguridad (recuperacion de contrasena olvidada).
    public string? SecurityQuestion { get; set; }
    // Contrasena maestra ACTUAL, cifrada con una clave derivada de la respuesta
    // (base64 de salt:nonce:tag:ciphertext, ver CryptoHelper.EncryptBytes). Se
    // vuelve a generar cada vez que cambia la contrasena, para que la respuesta
    // de seguridad siempre pueda recuperar la contrasena vigente.
    public string? RecoveryBlob { get; set; }
}

/// <summary>
/// Guarda la configuracion (incluido el hash de la contrasena maestra) en
/// %AppData%\SecureMaid\config.json. La contrasena en si NUNCA se guarda,
/// solo un hash PBKDF2 con salt aleatorio.
/// </summary>
public static class PasswordManager
{
    public static readonly string AppDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureMaid");

    private static readonly string ConfigPath = Path.Combine(AppDataFolder, "config.json");

    public static AppSettings Settings { get; private set; } = new();

    public static void Load()
    {
        Directory.CreateDirectory(AppDataFolder);
        if (File.Exists(ConfigPath))
        {
            var json = File.ReadAllText(ConfigPath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(AppDataFolder);
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public static bool HasMasterPassword() => !string.IsNullOrEmpty(Settings.MasterPasswordHash);

    public static void SetMasterPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = CryptoHelper.DeriveKey(password, salt);
        Settings.MasterPasswordHash = $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
        Save();
    }

    public static bool VerifyMasterPassword(string password)
    {
        if (!HasMasterPassword()) return false;
        var parts = Settings.MasterPasswordHash!.Split(':');
        byte[] salt = Convert.FromBase64String(parts[0]);
        byte[] storedHash = Convert.FromBase64String(parts[1]);
        byte[] attempt = CryptoHelper.DeriveKey(password, salt);
        return CryptographicOperations.FixedTimeEquals(storedHash, attempt);
    }

    // ---------------- Pregunta de seguridad / recuperacion ----------------

    public static bool HasSecurityQuestion() =>
        !string.IsNullOrEmpty(Settings.SecurityQuestion) && !string.IsNullOrEmpty(Settings.RecoveryBlob);

    /// <summary>Largo minimo exigido para la respuesta de seguridad. Esto NO evita
    /// que alguien elija una respuesta adivinable (eso depende del usuario: por
    /// diseno, quien sepa la respuesta puede recuperar/cambiar la contrasena
    /// maestra, ver la advertencia en la pantalla donde se configura), pero 2
    /// caracteres era demasiado permisivo incluso para eso. No es una garantia de
    /// seguridad por si sola, solo reduce el peor caso.</summary>
    public const int MinSecurityAnswerLength = 8;

    /// <summary>Iteraciones de PBKDF2 usadas SOLO para la clave derivada de la
    /// respuesta de seguridad (no para el resto del cifrado). Es deliberadamente
    /// mucho mas alta que las de CryptoHelper: la respuesta a una pregunta de
    /// seguridad suele tener mucha menos entropia que una contrasena, y quien
    /// tenga una copia de config.json puede probar respuestas offline SIN limite
    /// de intentos (no hay bloqueo posible sobre datos que ya salieron del control
    /// de la app). Subir el costo por intento es la unica mitigacion disponible a
    /// nivel de esta app contra ese escenario -- no lo elimina, solo lo encarece.
    /// Como esta derivacion solo ocurre una vez por intento de login/recuperacion
    /// (nunca en un bucle sobre archivos grandes), el costo extra es imperceptible
    /// para el usuario legitimo.</summary>
    private const int RecoveryIterations = 1_000_000;

    /// <summary>Las respuestas se normalizan (sin espacios extra, sin mayusculas) para
    /// que no importen esos detalles al momento de recuperar la contrasena.</summary>
    private static string NormalizeAnswer(string answer) => answer.Trim().ToLowerInvariant();

    /// <summary>Guarda la pregunta y cifra la contrasena maestra vigente con una clave
    /// derivada de la respuesta. Debe llamarse cada vez que la contrasena cambia
    /// (creacion o cambio), para que la recuperacion siga funcionando.</summary>
    public static void SetSecurityQuestion(string question, string answer, string currentPassword)
    {
        Settings.SecurityQuestion = question.Trim();
        byte[] blob = CryptoHelper.EncryptBytes(
            Encoding.UTF8.GetBytes(currentPassword), NormalizeAnswer(answer), RecoveryIterations);
        Settings.RecoveryBlob = Convert.ToBase64String(blob);
        Save();
    }

    /// <summary>Igual que SetSecurityQuestion pero solo re-cifra la contrasena vigente
    /// (mantiene la misma pregunta). Se usa al cambiar la contrasena maestra.</summary>
    public static void UpdateRecoveryBlob(string newPassword, string answer)
    {
        if (!HasSecurityQuestion()) return;
        byte[] blob = CryptoHelper.EncryptBytes(
            Encoding.UTF8.GetBytes(newPassword), NormalizeAnswer(answer), RecoveryIterations);
        Settings.RecoveryBlob = Convert.ToBase64String(blob);
        Save();
    }

    /// <summary>Intenta recuperar la contrasena maestra vigente a partir de la
    /// respuesta a la pregunta de seguridad. Devuelve null si la respuesta es
    /// incorrecta o no hay pregunta configurada.
    ///
    /// Nota de diseno (no es un bug, pero hay que tenerlo claro): esta respuesta
    /// funciona, en la practica, como una segunda contrasena capaz de recuperar
    /// la contrasena maestra completa -- no es solo una verificacion de
    /// identidad. Quien la conozca (o la adivine/pruebe offline sobre una copia
    /// de config.json) puede recuperar el acceso igual que con la contrasena
    /// real. La UI donde se configura ya se lo advierte al usuario; aqui solo se
    /// documenta para quien lea el codigo.</summary>
    public static string? TryRecoverPassword(string answer)
    {
        if (!HasSecurityQuestion()) return null;
        try
        {
            byte[] blob = Convert.FromBase64String(Settings.RecoveryBlob!);
            byte[]? plain = CryptoHelper.DecryptBytes(blob, NormalizeAnswer(answer), RecoveryIterations);
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
