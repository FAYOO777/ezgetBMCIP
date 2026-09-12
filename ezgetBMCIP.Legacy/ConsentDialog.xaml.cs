using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EzGetBmcIp.Legacy
{
    internal partial class ConsentDialog : Window
    {
        internal ConsentDialog(ConsentNotice notice)
        {
            InitializeComponent();
            DataContext = notice;
            ApplyNoticeLayout(notice);
            Loaded += Dialog_Loaded;
            ContentRendered += Dialog_ContentRendered;
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
        }

        private void ApplyNoticeLayout(ConsentNotice notice)
        {
            var isNetwork = notice.HasNetworkChangeSummary;
            FirewallWarningBorder.Visibility = notice.ShowLegacyWarning ? Visibility.Visible : Visibility.Collapsed;
            NetworkChangeSummaryPanel.Visibility = isNetwork ? Visibility.Visible : Visibility.Collapsed;
            GenericItemsBorder.Visibility = isNetwork ? Visibility.Collapsed : Visibility.Visible;
            GenericActionPanel.Visibility = isNetwork ? Visibility.Collapsed : Visibility.Visible;
            NetworkActionFooter.Visibility = isNetwork ? Visibility.Visible : Visibility.Collapsed;
            NetworkAgreeButton.IsEnabled = false;
            AgreeButton.IsEnabled = false;
        }

        private void Dialog_Loaded(object sender, RoutedEventArgs e)
        {
            FocusSafeCancel();
        }

        private void Dialog_ContentRendered(object sender, System.EventArgs e)
        {
            FocusSafeCancel();
        }

        private void FocusSafeCancel()
        {
            var notice = DataContext as ConsentNotice;
            if (notice == null || (!notice.PreferSafeDefault && !notice.HasNetworkChangeSummary))
                return;

            var button = notice.HasNetworkChangeSummary ? NetworkCancelButton : CancelButton;
            if (!button.Focus())
                Dispatcher.BeginInvoke(new Action(() => button.Focus()), DispatcherPriority.Input);
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
}
