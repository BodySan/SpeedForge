using SpeedForge.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SpeedForge.Desktop;

public partial class MainWindow
{
    private TestOptions ReadBeelineOptions(TestOptions options)
    {
        if (BeelineTariff.SelectedItem is not int tariff)
        { AdvancedPanel.IsExpanded = true; BeelineTariff.Focus(); throw new InvalidDataException("Укажите скорость, предусмотренную тарифом, в Мбит/с"); }
        return BeelineTest.Configure(options, tariff);
    }
    private void BeelineTariff_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        SaveNetworkPreferences(); UpdateRateSummary();
    }
    private void UpdateBeelineControls(ServerEntry server)
    {
        bool beeline = BeelineTest.Applies(server);
        OperatorPanel.Visibility = CityPanel.Visibility = beeline ? Visibility.Collapsed : Visibility.Visible;
        BeelineTariffPanel.Visibility = beeline ? Visibility.Visible : Visibility.Collapsed;
        ParallelPanel.Visibility = DirectionPanel.Visibility = ProtocolPanel.Visibility = RatePanel.Visibility =
            RateFamilyPanel.Visibility = ClearRateButton.Visibility = BothCheck.Visibility = beeline ? Visibility.Collapsed : Visibility.Visible;
        Iperf2ProfilesCheck.Visibility = Visibility.Collapsed;
        ChartTitle.Text = beeline ? "UDP-отправка во времени" : "Скорость во времени";
        UploadHeading.Text = beeline ? "↑  UDP-ОТПРАВКА" : "↑  С ПК";
        UploadUnits.Text = beeline ? "Мбит/с · отправлено с ПК" : "Мбит/с · отдача";
        Grid.SetColumn(DurationPanel, beeline ? 0 : 4); Grid.SetColumnSpan(DurationPanel, beeline ? 7 : 1);
        if (beeline) { BothCheck.IsChecked = true; DirectionCombo.SelectedIndex = 1; TransportCombo.SelectedIndex = 1; }
    }
    private void ApplySmallMetricColors()
    {
        string text = PacketLossValue.Text;
        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            PacketLossValue.Inlines.Clear();
            PacketLossValue.Inlines.Add(new Run(text[..(colon + 1)]) { Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("LatencyTone") });
            PacketLossValue.Inlines.Add(new Run(text[(colon + 1)..]));
        }
    }
}
