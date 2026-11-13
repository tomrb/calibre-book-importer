using System.Diagnostics;

namespace CalibreBookImporter;

internal sealed record CalibreToolPaths(string CalibreDb, string? EbookConvert, string? EbookMeta)
{
    public static CalibreToolPaths Find(string? calibreDirectory = null)
    {
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(calibreDirectory))
        {
            directories.Add(calibreDirectory);
        }
        else
        {
            directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            if (OperatingSystem.IsWindows())
            {
                directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2"));
                directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Calibre2"));
            }
        }

        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var candidates = directories.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(directory => new CalibreToolPaths(
                Path.Combine(directory, "calibredb" + suffix),
                Path.Combine(directory, "ebook-convert" + suffix),
                Path.Combine(directory, "ebook-meta" + suffix)))
            .FirstOrDefault(candidate => File.Exists(candidate.CalibreDb));
        if (candidates is null)
        {
            throw new CalibreToolingException(
                "Could not locate calibredb. Install Calibre, add its folder to PATH, or pass --calibre-dir.");
        }

        return candidates with
        {
            EbookConvert = File.Exists(candidates.EbookConvert) ? candidates.EbookConvert : null,
            EbookMeta = File.Exists(candidates.EbookMeta) ? candidates.EbookMeta : null
        };
    }

    public static ProcessResult Run(string executable, IEnumerable<string> arguments, int timeoutMilliseconds = 120000)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new CalibreToolingException($"Could not start {Path.GetFileName(executable)}.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new CalibreToolingException($"Could not start {Path.GetFileName(executable)}: {exception.Message}", exception);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new CalibreToolingException($"{Path.GetFileName(executable)} exceeded its {timeoutMilliseconds / 1000}-second timeout.");
        }
        return new ProcessResult(process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class CalibreToolingException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
