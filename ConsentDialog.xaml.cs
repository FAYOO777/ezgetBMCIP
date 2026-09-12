using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace EzGetBmcIp;

internal partial class ConsentDialog : FluentWindow
{
    internal ConsentDialog(ConsentNotice notice)
    {
        InitializeComponent();
        DataContext = notice;
        ApplyButtonPresentation(notice.PreferSafeDefault);
        ApplyNoticeLayout(notice);
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
        var enabled = sender is CheckBox checkBox && checkBox.IsChecked == true;
        if (ReferenceEquals(sender, NetworkAcknowledgementCheckBox))
        {
            NetworkAgreeButton.IsEnabled = enabled;
            return;
        }

        AgreeButton.IsEnabled = enabled;
        AgreeSecondaryButton.IsEnabled = enabled;
    }

    private void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConsentNotice notice ||
            (!notice.PreferSafeDefault && !notice.HasNetworkChangeSummary))
            return;

        // Put the safe exit action first, without stealing focus from the
        // acknowledgement checkbox or enabling the confirmation action.
        FocusSafeCancel();
    }

    private void ApplyButtonPresentation(bool preferSafeDefault)
    {
        // Set these explicitly as well as in XAML. The dialog is constructed
        // before its inherited DataContext is attached, and an explicit value
        // keeps the safety ordering deterministic on older WPF binding stacks.
        CancelButton.Visibility = preferSafeDefault ? Visibility.Visible : Visibility.Collapsed;
        CancelSecondaryButton.Visibility = preferSafeDefault ? Visibility.Collapsed : Visibility.Visible;
        AgreeButton.Visibility = preferSafeDefault ? Visibility.Collapsed : Visibility.Visible;
        AgreeSecondaryButton.Visibility = preferSafeDefault ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyNoticeLayout(ConsentNotice notice)
    {
        // Keep the layout choice deterministic for the formal dialog. The
        // shared notice model is also used by Legacy, whose XAML is separate.
        FirewallWarningBorder.Visibility = notice.ShowLegacyWarning
            ? Visibility.Visible
            : Visibility.Collapsed;
        NetworkChangeSummaryPanel.Visibility = notice.HasNetworkChangeSummary
            ? Visibility.Visible
            : Visibility.Collapsed;
        GenericItemsBorder.Visibility = notice.HasNetworkChangeSummary
            ? Visibility.Collapsed
            : Visibility.Visible;
        GenericActionPanel.Visibility = notice.HasNetworkChangeSummary
            ? Visibility.Collapsed
            : Visibility.Visible;
        NetworkActionFooter.Visibility = notice.HasNetworkChangeSummary
            ? Visibility.Visible
            : Visibility.Collapsed;
        NetworkAgreeButton.IsEnabled = false;
    }

    private void Dialog_ContentRendered(object? sender, EventArgs e)
    {
        FocusSafeCancel();
    }

    private void FocusSafeCancel()
    {
        if (DataContext is not ConsentNotice notice ||
            (!notice.PreferSafeDefault && !notice.HasNetworkChangeSummary))
            return;

        var safeCancelButton = notice.HasNetworkChangeSummary
            ? NetworkCancelButton
            : CancelButton;
        if (!safeCancelButton.Focus())
            Dispatcher.BeginInvoke(new Action(() => safeCancelButton.Focus()), DispatcherPriority.Input);
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
