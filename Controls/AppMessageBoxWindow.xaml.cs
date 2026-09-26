using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SecureMaid.Controls;

public partial class AppMessageBoxWindow : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public AppMessageBoxWindow(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        InitializeComponent();
        CaptionText.Text = caption;
        MessageText.Text = text;
        IconText.Text = IconFor(icon);
        BuildButtons(button);
    }

    private static string IconFor(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Error => "⛔",
        MessageBoxImage.Warning => "⚠️",
        MessageBoxImage.Question => "❓",
        MessageBoxImage.Information => "ℹ️",
        _ => "🔒",
    };

    private void BuildButtons(MessageBoxButton button)
    {
        switch (button)
        {
            case MessageBoxButton.YesNo:
                AddButton("Sí", MessageBoxResult.Yes, primary: true);
                AddButton("No", MessageBoxResult.No, primary: false);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Sí", MessageBoxResult.Yes, primary: true);
                AddButton("No", MessageBoxResult.No, primary: false);
                AddButton("Cancelar", MessageBoxResult.Cancel, primary: false);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Aceptar", MessageBoxResult.OK, primary: true);
                AddButton("Cancelar", MessageBoxResult.Cancel, primary: false);
                break;
            default:
                AddButton("Aceptar", MessageBoxResult.OK, primary: true);
                break;
        }
    }

    private void AddButton(string content, MessageBoxResult result, bool primary)
    {
        var btn = new Button
        {
            Content = content,
            Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"),
            Margin = new Thickness(6, 0, 6, 0),
            MinWidth = 90
        };
        btn.Click += (_, __) => { Result = result; Close(); };
        ButtonsPanel.Children.Add(btn);
        if (primary)
        {
            Loaded += (_, __) => btn.Focus();
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
