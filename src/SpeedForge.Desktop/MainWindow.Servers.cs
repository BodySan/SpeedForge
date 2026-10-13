using SpeedForge.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SpeedForge.Desktop;

public partial class MainWindow
{
    private void Catalog_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CatalogHost == null) return;
        bool selected = CatalogGrid.SelectedItem is ServerEntry;
        CatalogHost.IsEnabled = CatalogSaveButton.IsEnabled = selected;
        if (CatalogGrid.SelectedItem is ServerEntry entry)
        {
            CatalogHost.Text = entry.Host;
            CatalogPort.Text = "Порт: " + string.Join(", ", entry.Ports.Length == 0 ? [entry.Port] : entry.Ports);
            CatalogResetButton.IsEnabled = entry.HasLocalOverride;
        }
        else { CatalogHost.Clear(); CatalogPort.Text = "Выберите сервер в списке"; CatalogResetButton.IsEnabled = false; }
    }

    private void CatalogSave_Click(object sender, RoutedEventArgs e) => SaveCatalogAddress(false);
    private void CatalogReset_Click(object sender, RoutedEventArgs e) => SaveCatalogAddress(true);

    private void SaveCatalogAddress(bool reset)
    {
        if (_running != null || CatalogGrid.SelectedItem is not ServerEntry entry) return;
        try
        {
            string? active = (ServerCombo.SelectedItem as ServerEntry)?.Id;
            string? comparison = (CompareCombo.SelectedItem as ServerEntry)?.Id;
            _servers = _serverOverrides.Save(entry.Id, reset ? null : CatalogHost.Text);
            _serverSelectionGeneration++; // A pending GeoIP result must not replace the user's correction.
            if (active == entry.Id) { _displayGeneration++; ResetResult(); }
            Refilter(false);
            CompareCombo.ItemsSource = _servers;
            CompareCombo.SelectedItem = _servers.FirstOrDefault(x => x.Id == comparison);
            CatalogSearch_Changed(CatalogSearch, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
            if (!CatalogGrid.Items.Cast<ServerEntry>().Any(x => x.Id == entry.Id)) CatalogSearch.Clear();
            CatalogGrid.SelectedItem = _servers.Single(x => x.Id == entry.Id);
            CatalogGrid.ScrollIntoView(CatalogGrid.SelectedItem);
            CatalogEditStatus.Text = reset ? "Исходный адрес восстановлен" : "Адрес сохранён. Он будет использоваться при следующих проверках.";
            UpdateProfile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { CatalogEditStatus.Text = "Не удалось сохранить: " + ex.Message; }
    }
}
