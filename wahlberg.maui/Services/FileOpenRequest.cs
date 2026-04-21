using System.Collections.Concurrent;

namespace Wahlberg.Services;

public static class FileOpenRequest
{
    private static readonly ConcurrentQueue<string> _pending = new();
    private static Func<string, Task>? _handler;

    // Buffers requests raised before the UI subscribes and drains them on subscribe.
    public static event Func<string, Task>? FileRequested
    {
        add
        {
            _handler += value;
            while (_pending.TryDequeue(out var path))
                _ = value?.Invoke(path);
        }
        remove => _handler -= value;
    }

    internal static void Raise(string filePath)
    {
        if (_handler != null)
            _ = _handler(filePath);
        else
            _pending.Enqueue(filePath);
    }
}
