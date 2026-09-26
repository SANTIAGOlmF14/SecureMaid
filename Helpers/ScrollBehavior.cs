using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SecureMaid.Helpers;

/// <summary>
/// Un TextBox o PasswordBox de una sola linea igual trae por dentro un
/// ScrollViewer (PART_ContentHost). Aunque no tenga nada que desplazar, ese
/// ScrollViewer interno "atrapa" la rueda del mouse y no deja que el evento
/// suba hasta el ScrollViewer de la pantalla (Ajustes, Camuflar, Recientes),
/// asi que el scroll se frena en seco apenas el mouse pasa sobre un campo.
///
/// La solucion es enganchar esto en PreviewMouseWheel del ScrollViewer
/// contenedor: como PreviewMouseWheel es un evento de tunel (baja desde la
/// ventana hacia el control bajo el mouse), lo atendemos ahi ANTES de que
/// llegue al campo de texto, movemos el scroll nosotros mismos y marcamos el
/// evento como manejado para que no siga bajando.
///
/// Cuidado con los ComboBox: su lista desplegable vive en un Popup, que NO
/// es descendiente visual de este ScrollViewer aunque se dibuje encima.
/// Antes de redirigir el scroll hay que confirmar que el origen del evento
/// (e.OriginalSource) SI es parte de este ScrollViewer; si no lo es (por
/// ejemplo, el mouse esta sobre el Popup del combo), no se toca el evento y
/// se deja que el propio ScrollViewer del Popup lo maneje, para no "robarle"
/// el scroll a la lista de opciones.
/// </summary>
public static class ScrollBehavior
{
    public static void RedirectToScrollViewer(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (!IsDescendantOf(e.OriginalSource as DependencyObject, sv)) return;

        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
