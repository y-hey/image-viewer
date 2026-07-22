using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ImageViewer;

public sealed class FolderLock : IDisposable
{
    private readonly string _lockPath;
    private readonly System.Threading.Timer _heartbeat;
    private bool _disposed;

    public record LockInfo(string Machine, int Pid, string User, DateTime Timestamp);

    private FolderLock(string lockPath)
    {
        _lockPath = lockPath;
        WriteLock();
        _heartbeat = new System.Threading.Timer(_ => WriteLock(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public static (FolderLock? handle, string? error) TryAcquire(string rootPath)
    {
        var lockPath = Path.Combine(rootPath, "_db", ".lock");
        var existing = ReadLock(lockPath);

        if (existing != null)
        {
            if (existing.Machine == Environment.MachineName && IsProcessAlive(existing.Pid))
                return (null, $"このPC上の別インスタンス (PID {existing.Pid}) が開いています。");

            var age = DateTime.UtcNow - existing.Timestamp;
            if (age.TotalSeconds < 45)
                return (null, $"{existing.Machine} ({existing.User}) が {age.TotalSeconds:F0}秒前にアクセス中です。\n強制的に開くにはロックファイルを手動で削除してください:\n{lockPath}");
        }

        return (new FolderLock(lockPath), null);
    }

    public static (FolderLock? handle, string? error) ForceAcquire(string rootPath)
    {
        var lockPath = Path.Combine(rootPath, "_db", ".lock");
        return (new FolderLock(lockPath), null);
    }

    private void WriteLock()
    {
        if (_disposed) return;
        try
        {
            var info = new LockInfo(Environment.MachineName, Environment.ProcessId, Environment.UserName, DateTime.UtcNow);
            var json = JsonSerializer.Serialize(info);
            File.WriteAllText(_lockPath, json);
        }
        catch { }
    }

    private static LockInfo? ReadLock(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LockInfo>(json);
        }
        catch { return null; }
    }

    private static bool IsProcessAlive(int pid)
    {
        try { Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeat.Dispose();
        try { File.Delete(_lockPath); } catch { }
    }
}
