namespace SecureMaid.Helpers;

/// <summary>
/// Guarda la contrasena maestra SOLO en memoria durante la sesion (nunca en
/// disco). Segun la configuracion del usuario (Settings.RequirePasswordOnlyForVault),
/// la UI puede optar por no guardarla aqui y pedirla cada vez que se entra a
/// la Caja Fuerte o se oculta/camufla algo, en vez de una sola vez al abrir la app.
/// </summary>
public static class Session
{
    public static string? CurrentPassword { get; set; }

    public static void Clear() => CurrentPassword = null;
}
