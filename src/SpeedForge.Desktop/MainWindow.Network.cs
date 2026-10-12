using SpeedForge.Core;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace SpeedForge.Desktop;

public partial class MainWindow
{
    private readonly NetworkIdentityClient _network = new();
    private string _preferencesPath = "";
    private bool _loadNetworkOnStart, _applyingNetworkSelection;
    private int _serverSelectionGeneration, _networkGeneration;
    private CancellationTokenSource? _networkRefresh;
    private Task? _networkStartupTask;
    private NetworkIdentity? _networkIdentity;

    private void LoadNetworkPreferences(bool loadOnStart)
    {
        _loadNetworkOnStart = loadOnStart;
        _preferencesPath = Path.Combine(_store.DirectoryPath, "preferences.json");
        try
        {
            if (File.Exists(_preferencesPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_preferencesPath));
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("GeoIpEnabled", out var enabled)
                    && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    GeoIpCheck.IsChecked = enabled.GetBoolean();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private void GeoIp_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        try
        {
            string temp = _preferencesPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { GeoIpEnabled = GeoIpCheck.IsChecked == true }));
            File.Move(temp, _preferencesPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Настройка геолокации не сохранена"); }
        if (_loadNetworkOnStart) _networkStartupTask = RefreshNetworkAsync();
    }
    private async Task RefreshNetworkAsync(Func<bool,CancellationToken,Task<NetworkIdentity>>? lookup = null)
    {
        int generation = ++_networkGeneration, selection = _serverSelectionGeneration;
        bool geo = GeoIpCheck.IsChecked == true;
        _networkRefresh?.Cancel(); _networkRefresh?.Dispose();
        _networkRefresh = new CancellationTokenSource();
        var token = _networkRefresh.Token;
        ConnectionInfo.Text = "IP: определяем…";
        try
        {
            var identity = await (lookup ?? _network.GetAsync)(geo, token);
            if (generation != _networkGeneration || token.IsCancellationRequested) return;
            ApplyNetworkIdentity(identity, geo && GeoIpCheck.IsChecked == true, selection);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        { if (generation == _networkGeneration) { ConnectionInfo.Text = "IP: недоступен"; Log("Определение IP: " + ex.GetType().Name); } }
    }
    private void ApplyNetworkIdentity(NetworkIdentity identity, bool geo, int selection)
    {
        _networkIdentity = identity;
        string address = identity.ExternalIp ?? "недоступен";
        ConnectionInfo.Text = $"IP: {address}";
        if (identity.HttpIp != null && identity.HttpIp != identity.ExternalIp && identity.ExternalIp != null)
            ConnectionInfo.Text += $" · HTTPS: {identity.HttpIp}";
        if (geo)
        {
            if (identity.City != null) ConnectionInfo.Text += " · " + identity.City;
            if (identity.Organization != null) ConnectionInfo.Text += " · " + identity.Organization;
            else if (identity.Asn != null) ConnectionInfo.Text += " · AS" + identity.Asn;
        }
        string explanation = $"Внешний IP: {address}. Источник: {identity.Source}.";
        if (geo) explanation += " Геолокация по IP приблизительная; VPN может менять выбранный город.";
        if (geo && identity.Location?.Valid == true && selection == _serverSelectionGeneration && _running == null
            && string.IsNullOrWhiteSpace(CustomHost.Text) && string.IsNullOrWhiteSpace(CustomUrl.Text))
        {
            var nearest = NearestServer.Choose(_servers, identity.Location);
            if (nearest != null)
            {
                _applyingNetworkSelection = true;
                try { SelectServer(nearest.Server); }
                finally { _applyingNetworkSelection = false; }
                explanation += $" Автовыбор: {nearest.Server.City}, примерно {nearest.Kilometres:F0} км до города сервера.";
            }
        }
        ConnectionInfo.ToolTip = explanation;
    }
    protected override void OnClosed(EventArgs e)
    {
        _networkRefresh?.Cancel(); _networkRefresh?.Dispose(); _network.Dispose();
        base.OnClosed(e);
    }
    public async Task<bool> VerifyNetworkAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        if (_networkStartupTask != null) await _networkStartupTask;
        SavePreview(Path.Combine(directory, "network.png"));
        WindowPreview.Save(this, Path.Combine(directory, "network-window.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "network.json"), JsonSerializer.Serialize(new
        { Identity = _networkIdentity, Display = ConnectionInfo.Text, Server = CurrentServer().Id, GeoIpEnabled = GeoIpCheck.IsChecked }, VerificationJson));
        return _networkIdentity?.ExternalIp != null;
    }
}
