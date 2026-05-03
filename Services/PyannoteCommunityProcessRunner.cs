using System.Diagnostics;
using System.IO;
using System.Text;

namespace AudioScript.Services;

public interface IPyannoteCommunityProcessRunner
{
    Task<PyannoteCommunityProcessResult> RunAsync(
        string pythonExecutablePath,
        string runnerScriptPath,
        string modelDirectoryPath,
        string audioFilePath,
        CancellationToken cancellationToken);
}

public sealed record PyannoteCommunityProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError
);

public sealed class PyannoteCommunityProcessRunner : IPyannoteCommunityProcessRunner
{
    public async Task<PyannoteCommunityProcessResult> RunAsync(
        string pythonExecutablePath,
        string runnerScriptPath,
        string modelDirectoryPath,
        string audioFilePath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(runnerScriptPath) ?? AppContext.BaseDirectory,
            },
            EnableRaisingEvents = true,
        };

        process.StartInfo.ArgumentList.Add(runnerScriptPath);
        process.StartInfo.ArgumentList.Add(modelDirectoryPath);
        process.StartInfo.ArgumentList.Add(audioFilePath);

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start bundled pyannote diarization process.");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            return new PyannoteCommunityProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cleanup for canceled diarization subprocesses.
            }

            throw;
        }
    }
}
