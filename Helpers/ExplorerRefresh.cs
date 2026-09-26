using System.Runtime.InteropServices;

namespace SecureMaid.Helpers;

/// <summary>
/// Windows Explorer no siempre se entera solo de los cambios que la app hace
/// por debajo (mover, ocultar, camuflar, crear accesos directos...), y hasta
/// que el usuario hace clic derecho y "Actualizar" a mano, la ventana del
/// Explorador se queda mostrando cosas que ya no son ciertas. Esto avisa a
/// Windows del cambio, como si el usuario hubiera actualizado el solo.
/// </summary>
public static class ExplorerRefresh
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // SHCNE_ASSOCCHANGED: el "refresco general", el mismo que usa Windows cuando
    // cambian iconos/asociaciones a nivel de sistema. Es un poco más "de martillo"
    // que notificar una ruta puntual, pero es el más confiable para forzar que
    // *todas* las ventanas del Explorador abiertas se refresquen solas.
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0x0000;

    /// <summary>Llamar despues de cualquier operacion que cree, borre, mueva,
    /// renombre o cambie el icono de un archivo/carpeta desde la app (agregar a
    /// la caja fuerte, ocultar, camuflar, descamuflar, crear accesos directos...),
    /// para que el Explorador de Windows se refresque solo.</summary>
    public static void RefreshAll()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { /* si esto falla no debe romper la operacion real que ya se hizo */ }
    }
}
