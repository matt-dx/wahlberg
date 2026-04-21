namespace Wahlberg.Services;

public static class FileOpenRequest
{
    public static event Action<string>? FileRequested;
    internal static void Raise(string filePath) => FileRequested?.Invoke(filePath);
}
