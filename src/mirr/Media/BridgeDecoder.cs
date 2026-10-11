using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using mirr.Engine;

namespace mirr.Media;

public sealed class BridgeDecoder : IDisposable
{
    private const string ScriptResourceName = "mirr_bridge.py";

    private readonly object _writeLock = new();
    private Process? _process;
    private EngineCommand? _command;
    private CodecFamily _family = CodecFamily.Hevc;
    private volatile bool _disposed;
    private volatile bool _stopping;

    public event Action<FramePresentation>? FrameReady;
    public event Action<string>? LogLine;
    public event Action<string>? Faulted;

    public static string ExtractScript()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mirr");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ScriptResourceName);
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptResourceName)
            ?? throw new InvalidOperationException("The embedded decode bridge script is missing.");
        using var reader = new MemoryStream();
        resource.CopyTo(reader);
        byte[] script = reader.ToArray();

        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(script))
            {
                return path;
            }
            File.WriteAllBytes(path, script);
        }
        catch (IOException)
        {
            if (!File.Exists(path))
            {
                throw;
            }
        }
        return path;
    }

    public bool IsRunning => _process is { HasExited: false };

    public void Start(EngineCommand command, CodecFamily family)
    {
        _command = command;
        _family = family;
        Spawn();
    }

    private void Spawn()
    {
        if (_disposed || _command is null)
        {
            return;
        }
        Stop();

        string scriptPath = ExtractScript();
        string family = _family == CodecFamily.Hevc ? "hevc" : "h264";
        ProcessStartInfo startInfo = _command.ToScriptStartInfo(scriptPath, ["--codec", family]);
        startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        _stopping = false;
        _process = process;
        _ = Task.Run(() => ReadFramesAsync(process));
        _ = Task.Run(() => ReadLogAsync(process));
    }

    public void Feed(byte[] annexB)
    {
        Process? process = _process;
        if (process is null || annexB.Length == 0)
        {
            return;
        }
        lock (_writeLock)
        {
            try
            {
                Stream stdin = process.StandardInput.BaseStream;
                Span<byte> header = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(annexB.Length + 1));
                stdin.Write(header);
                stdin.WriteByte(1);
                stdin.Write(annexB);
                stdin.Flush();
            }
            catch (Exception exception)
            {
                if (!_disposed && !process.HasExited)
                {
                    LogLine?.Invoke($"bridge write failed: {exception.Message}");
                }
            }
        }
    }

    private async Task ReadFramesAsync(Process process)
    {
        byte[] header = new byte[16];
        try
        {
            Stream stdout = process.StandardOutput.BaseStream;
            while (!_disposed)
            {
                if (!await ReadExactAsync(stdout, header).ConfigureAwait(false))
                {
                    break;
                }
                uint total = BinaryPrimitives.ReadUInt32BigEndian(header);
                int width = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                int height = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
                int stride = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
                int payloadLength = (int)total - 12;
                if (width <= 0 || height <= 0 || stride <= 0 || payloadLength <= 0 || payloadLength > 64 * 1024 * 1024)
                {
                    break;
                }
                var pixels = new byte[payloadLength];
                if (!await ReadExactAsync(stdout, pixels).ConfigureAwait(false))
                {
                    break;
                }
                FrameReady?.Invoke(new FramePresentation(pixels, width, height, stride, HasAlpha: true));
            }
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                LogLine?.Invoke($"bridge read failed: {exception.Message}");
            }
        }
        if (!_disposed && !_stopping)
        {
            Faulted?.Invoke("decode bridge stopped");
        }
    }

    private async Task ReadLogAsync(Process process)
    {
        try
        {
            while (!_disposed)
            {
                string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (line.Length > 0)
                {
                    LogLine?.Invoke($"bridge: {line}");
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] target)
    {
        int offset = 0;
        while (offset < target.Length)
        {
            int read = await stream.ReadAsync(target.AsMemory(offset)).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    public void Stop()
    {
        _stopping = true;
        Process? process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                }
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception)
        {
        }
        process.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
