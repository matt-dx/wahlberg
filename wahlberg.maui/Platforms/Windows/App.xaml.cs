using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Wahlberg.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        RegisterFileAssociations();
        base.OnLaunched(args);
    }

    private static void RegisterFileAssociations()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        const string progId = "Wahlberg.MarkdownFile";
        string[] extensions = [".md", ".markdown", ".mdown", ".mkd", ".mkdn"];

        using (var commandKey = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\{progId}\shell\open\command"))
        {
            var existing = commandKey.GetValue("") as string;
            var desired = $"\"{exePath}\" \"%1\"";
            if (existing == desired) return; // already registered at this path

            using var progIdKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{progId}");
            progIdKey.SetValue("", "Markdown File");

            commandKey.SetValue("", desired);
        }

        foreach (var ext in extensions)
        {
            using var extKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids");
            extKey.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags,
        IntPtr dwItem1, IntPtr dwItem2);
}
