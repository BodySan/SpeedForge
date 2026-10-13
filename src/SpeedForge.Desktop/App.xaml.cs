using System.Windows;
using System.IO;
namespace SpeedForge.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? dataRoot = e.Args.Length >= 2 && e.Args[0] is "--verify-ui" or "--verify-sandbox" or "--verify-reset" or "--verify-network" or "--verify-geo" or "--verify-servers"
            ? Path.Combine(Path.GetFullPath(e.Args[1]), "app-data") : null;
        MainWindow window;
        try { window = new MainWindow(dataRoot, e.Args.Length == 0 || e.Args[0] == "--verify-network"); }
        catch (Exception ex) when (dataRoot != null)
        {
            Directory.CreateDirectory(e.Args[1]);
            File.WriteAllText(Path.Combine(e.Args[1], "startup-fatal.txt"), ex.ToString());
            Shutdown(2); return;
        }
        MainWindow = window;
        window.Loaded += async (_, _) =>
        {
            if (e.Args.Length >= 2 && e.Args[0] == "--preview")
            {
                await Task.Delay(500);
                window.SavePreview(Path.GetFullPath(e.Args[1]));
                WindowPreview.Save(window, Path.ChangeExtension(Path.GetFullPath(e.Args[1]), ".window.png"));
                File.WriteAllText(Path.ChangeExtension(Path.GetFullPath(e.Args[1]), ".theme.json"), System.Text.Json.JsonSerializer.Serialize(new {
                    SetResults = NativeWindowTheme.Applied,
                    Dark = NativeWindowTheme.Read(window, NativeWindowTheme.DarkMode),
                    Caption = NativeWindowTheme.Read(window, NativeWindowTheme.Caption),
                    Border = NativeWindowTheme.Read(window, NativeWindowTheme.Border)
                }));
                Shutdown(0);
            }
            else if (e.Args.Length >= 3 && e.Args[0] == "--verify-ui")
            {
                try { Shutdown(await window.VerifyUiAsync(Path.GetFullPath(e.Args[1]), e.Args[2]) ? 0 : 1); }
                catch (Exception ex) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "fatal.txt"), ex.ToString()); Shutdown(2); }
            }
            else if (e.Args.Length >= 2 && e.Args[0] == "--verify-network")
            {
                try { Shutdown(await window.VerifyNetworkAsync(Path.GetFullPath(e.Args[1])) ? 0 : 1); }
                catch (Exception ex) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "fatal.txt"), ex.ToString()); Shutdown(2); }
            }
            else if (e.Args.Length >= 2 && e.Args[0] is "--verify-sandbox" or "--verify-reset" or "--verify-geo" or "--verify-servers")
            {
                try { Shutdown(await window.VerifySandboxAsync(Path.GetFullPath(e.Args[1]), e.Args[0] == "--verify-reset", e.Args[0] == "--verify-geo", e.Args[0] == "--verify-servers") ? 0 : 1); }
                catch (Exception ex) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "fatal.txt"), ex.ToString()); Shutdown(2); }
            }
        };
        window.Show();
    }
}
