using System.IO.Pipes;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using Wahlberg.Services;

namespace Wahlberg.WinUI;

public partial class App : MauiWinUIApplication
{
    private static Mutex? _instanceMutex;
    private static CancellationTokenSource? _pipeServerCts;
    private const string MutexName = "WahlbergSingleInstance";
    private const string PipeName = "WahlbergIPC";

    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instanceMutex = new Mutex(true, $"{MutexName}_{Environment.UserName}", out bool isFirstInstance);

        if (!isFirstInstance)
        {
            var filePath = GetCommandLineFilePath();
            if (filePath != null) SendFileToExistingInstance(filePath);
            Environment.Exit(0);
            return;
        }

        RegisterFileAssociations();
        _pipeServerCts = new CancellationTokenSource();
        Task.Run(() => ListenForFileRequests(_pipeServerCts.Token));

        base.OnLaunched(args);
    }

    private static string? GetCommandLineFilePath()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Length >= 2 && File.Exists(args[1]) ? args[1] : null;
    }

    private static void SendFileToExistingInstance(string filePath)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", $"{PipeName}_{Environment.UserName}", PipeDirection.Out);
            pipe.Connect(1000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(filePath);
        }
        catch { /* exit silently if pipe unreachable */ }
    }

    private static async Task ListenForFileRequests(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    $"{PipeName}_{Environment.UserName}",
                    PipeDirection.In, 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe);
                var path = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrEmpty(path))
                    FileOpenRequest.Raise(path);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public static void Cleanup()
    {
        _pipeServerCts?.Cancel();
        _pipeServerCts?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _instanceMutex?.Dispose();
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
