using System.Windows;
using System.Windows.Input;

namespace PyConnectKiosk;

public partial class PinDialog : Window
{
    public string EnteredPin { get; private set; } = "";

    public PinDialog() => InitializeComponent();

    protected override void OnContentRendered(System.EventArgs e)
    {
        base.OnContentRendered(e);
        PinBox.Focus();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => Confirm();
    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Confirm();
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }

    private void Confirm()
    {
        EnteredPin   = PinBox.Password;
        DialogResult = true;
        Close();
    }
}
