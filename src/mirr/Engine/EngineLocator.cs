using System.Diagnostics;
using System.IO;

namespace mirr.Engine;

public sealed record EngineCommand(string FileName, string PrefixArguments, bool Direct)
{
    public ProcessStartInfo ToStartInfo(IEnumerable<string> arguments)
    {
        ProcessStartInfo startInfo = CreateStartInfo(RedirectStandardInput: false, RedirectStandardOutput: false);
        if (!Direct)
        {
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("pymobiledevice3");
        }
        AddArguments(startInfo, arguments);
        return startInfo;
    }

    public ProcessStartInfo ToScriptStartInfo(string scriptPath, IEnumerable<string> arguments)
    {
        if (Direct)
        {
            throw new InvalidOperationException("This engine command is not backed by a Python interpreter.");
        }
        ProcessStartInfo startInfo = CreateStartInfo(RedirectStandardInput: true, RedirectStandardOutput: true);
        startInfo.ArgumentList.Add(scriptPath);
        AddArguments(startInfo, arguments);
        return startInfo;
    }

    public ProcessStartInfo ToPythonStartInfo(IEnumerable<string> arguments)
    {
        if (Direct)
        {
            throw new InvalidOperationException("This engine command is not backed by a Python interpreter.");
        }
        ProcessStartInfo startInfo = CreateStartInfo(RedirectStandardInput: false, RedirectStandardOutput: false);
        AddArguments(startInfo, arguments);
        return startInfo;
    }

    private ProcessStartInfo CreateStartInfo(bool RedirectStandardInput, bool RedirectStandardOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = RedirectStandardInput,
            RedirectStandardOutput = RedirectStandardOutput,
            RedirectStandardError = true,
        };
        AddArguments(startInfo, PrefixArguments);
        return startInfo;
    }

    private static void AddArguments(ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (string token in arguments)
        {
            if (token.Length > 0)
            {
                startInfo.ArgumentList.Add(token);
            }
        }
    }

    private static void AddArguments(ProcessStartInfo startInfo, string arguments)
    {
        foreach (string token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(token);
        }
    }
}

public static class EngineLocator
{
    private const string ModuleName = "pymobiledevice3";

    public static event Action<string>? Diagnostic;

    public static async Task<EngineCommand?> Resolve(CancellationToken cancellation)
    {
        foreach (EngineCommand candidate in Candidates())
        {
            Trace($"probing engine: {candidate.FileName} {candidate.PrefixArguments}");
            if (await Probe(candidate, ["version"], requireVersion: true, cancellation))
            {
                Trace($"engine resolved: {candidate.FileName} {candidate.PrefixArguments}");
                return candidate;
            }
        }
        return null;
    }

    public static async Task<EngineCommand?> ResolvePython(CancellationToken cancellation)
    {
        foreach (EngineCommand candidate in Candidates().Where(c => !c.Direct))
        {
            if (await Probe(candidate, ["-c", "import av"], requireVersion: false, cancellation))
            {
                Trace($"python resolved: {candidate.FileName} {candidate.PrefixArguments}");
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<EngineCommand> Candidates()
    {
        if (FindOnPath("pymobiledevice3.exe") is { } exe)
        {
            yield return new EngineCommand(exe, string.Empty, Direct: true);
        }
        if (FindOnPath("pymobiledevice3.bat") is { } batch)
        {
            yield return new EngineCommand(batch, string.Empty, Direct: true);
        }
        if (FindOnPath("py.exe") is { } launcher)
        {
            foreach (string version in new[] { "-3.13", "-3.12", "-3.11", "-3.10" })
            {
                yield return new EngineCommand(launcher, version, Direct: false);
            }
            yield return new EngineCommand(launcher, string.Empty, Direct: false);
        }
        foreach (string python in FindPythons())
        {
            yield return new EngineCommand(python, string.Empty, Direct: false);
        }
    }

    private static string? FindOnPath(string name)
    {
        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    private static IEnumerable<string> FindPythons()
    {
        foreach (string name in new[] { "python.exe", "python3.exe" })
        {
            if (FindOnPath(name) is { } found)
            {
                yield return found;
            }
        }
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programs = Path.Combine(localAppData, "Programs", "Python");
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string root in new[] { programs, programFiles })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            string[] directories = SafeDirectories(root);
            foreach (string directory in directories)
            {
                string python = Path.Combine(directory, "python.exe");
                if (File.Exists(python))
                {
                    yield return python;
                }
            }
        }
    }

    private static string[] SafeDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root, "Python*");
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static async Task<bool> Probe(EngineCommand candidate, string[] arguments, bool requireVersion, CancellationToken cancellation)
    {
        try
        {
            using Process process = new();
            process.StartInfo = requireVersion
                ? candidate.ToStartInfo(arguments)
                : candidate.ToPythonStartInfo(arguments);
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            string text = await output;
            _ = await error;
            if (process.ExitCode != 0)
            {
                return false;
            }
            return !requireVersion
                || text.Split('\n', StringSplitOptions.TrimEntries).Any(line => Version.TryParse(line, out Version? parsed) && parsed is not null);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            Trace($"probe failed: {candidate.FileName} {candidate.PrefixArguments} ({exception.Message})");
            return false;
        }
    }

    private static void Trace(string message)
    {
        Diagnostic?.Invoke(message);
    }
}
