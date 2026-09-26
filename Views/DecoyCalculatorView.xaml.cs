using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SecureMaid.Helpers;

namespace SecureMaid.Views;

/// <summary>
/// Calculadora 100% funcional. Ademas, escucha el teclado fisico en segundo
/// plano: si el usuario escribe su contraseña maestra completa y presiona
/// Enter, se revela la app real. El resultado de la calculadora en pantalla
/// (lo que se ve) es independiente de ese buffer oculto, asi que a simple
/// vista solo parece una calculadora normal, incluso si alguien esta mirando
/// por encima del hombro mientras se escribe la contraseña con el teclado.
/// </summary>
public partial class DecoyCalculatorView : UserControl
{
    public event EventHandler? Unlocked;

    private double _accumulator = 0;
    private string? _pendingOp = null;
    private bool _freshEntry = true;
    private readonly StringBuilder _secretBuffer = new();

    public DecoyCalculatorView()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += (_, __) => Focus();
        PreviewTextInput += OnPreviewTextInput;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        _secretBuffer.Append(e.Text);
        if (_secretBuffer.Length > 128) _secretBuffer.Remove(0, _secretBuffer.Length - 128);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string typed = _secretBuffer.ToString();
            _secretBuffer.Clear();
            if (typed.Length > 0 && PasswordManager.VerifyMasterPassword(typed))
            {
                Session.CurrentPassword = typed;
                Unlocked?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (e.Key == Key.Escape)
        {
            _secretBuffer.Clear();
        }
        else if (e.Key == Key.Back && _secretBuffer.Length > 0)
        {
            _secretBuffer.Remove(_secretBuffer.Length - 1, 1);
        }
    }

    // ---------- Logica normal de calculadora (para que se vea real) ----------

    private void OnDigit(object sender, RoutedEventArgs e)
    {
        string digit = ((Button)sender).Content.ToString()!;
        if (_freshEntry || Display.Text == "0")
        {
            Display.Text = digit;
            _freshEntry = false;
        }
        else
        {
            Display.Text += digit;
        }
    }

    private void OnDot(object sender, RoutedEventArgs e)
    {
        if (_freshEntry) { Display.Text = "0."; _freshEntry = false; return; }
        if (!Display.Text.Contains('.')) Display.Text += ".";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Display.Text = "0";
        _accumulator = 0;
        _pendingOp = null;
        _freshEntry = true;
    }

    private void OnBackspace(object sender, RoutedEventArgs e)
    {
        if (Display.Text.Length > 1) Display.Text = Display.Text[..^1];
        else Display.Text = "0";
    }

    private void OnSign(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(Display.Text, out double v))
            Display.Text = (-v).ToString();
    }

    private void OnPercent(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(Display.Text, out double v))
            Display.Text = (v / 100).ToString();
    }

    private void OnOperator(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(Display.Text, out double v)) _accumulator = v;
        _pendingOp = ((Button)sender).Tag.ToString();
        _freshEntry = true;
    }

    private void OnEquals(object sender, RoutedEventArgs e)
    {
        if (_pendingOp == null || !double.TryParse(Display.Text, out double v)) return;
        double result = _pendingOp switch
        {
            "+" => _accumulator + v,
            "-" => _accumulator - v,
            "*" => _accumulator * v,
            "/" => v != 0 ? _accumulator / v : 0,
            _ => v
        };
        Display.Text = result.ToString();
        _pendingOp = null;
        _freshEntry = true;
    }
}
