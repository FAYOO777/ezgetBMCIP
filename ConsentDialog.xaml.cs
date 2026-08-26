using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace EzGetBmcIp;

internal partial class ConsentDialog : FluentWindow
{
    internal ConsentDialog(ConsentNotice notice)
    {
        InitializeComponent();
        DataContext = notice;
    }

    internal static bool ShowFor(Window owner, ConsentNotice notice)
    {
        var dialog = new ConsentDialog(notice)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true;
    }

    private void AcknowledgementChanged(object sender, RoutedEventArgs e)
    {
        AgreeButton.IsEnabled = AcknowledgementCheckBox.IsChecked == true;
    }

    private void Agree_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        DialogResult = false;
    }
}
