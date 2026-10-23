using System.IO;
using System.Text;

namespace MineLib.Core;

/// <summary>
/// 游戏日志写入器：批量写盘而不是每行 flush。
/// 几千行日志逐行刷盘在机械盘上能拖慢明显，但也不能攒太久（崩溃时丢日志），
/// 所以按「行数 + 时间」两个条件触发刷盘。
/// </summary>
public sealed class GameLogWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private int _pending;
    private long _lastFlushTicks;
    private bool _disposed;

    public const int FlushEveryLines = 64;
    public const int FlushEveryMs = 500;

    public string Path { get; }

    public GameLogWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = false };
        _lastFlushTicks = Environment.TickCount64;
    }

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception)
            {
                return;
            }
            _pending++;
            var now = Environment.TickCount64;
            if (_pending >= FlushEveryLines || now - _lastFlushTicks >= FlushEveryMs)
            {
                FlushLocked();
                _lastFlushTicks = now;
            }
        }
    }

    public void Flush()
    {
        lock (_lock) FlushLocked();
    }

    private void FlushLocked()
    {
        try { _writer.Flush(); } catch (Exception) { }
        _pending = 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer.Flush(); } catch (Exception) { }
            try { _writer.Dispose(); } catch (Exception) { }
        }
    }
}
