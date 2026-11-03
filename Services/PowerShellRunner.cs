using System.Diagnostics;
using System.Text;

namespace Deviso.Services;

public sealed class PowerShellRunner
{
    public async Task<string> RunAsync(string script, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedScript}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Windows PowerShell could not be started.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The disk query exceeded {timeout.TotalMinutes:0} minutes.");
            }

            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? "The disk query failed." : error.Trim());
        }

        return output.Trim();
    }
}
