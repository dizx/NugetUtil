using System.Diagnostics;

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool whatIf,
        IReadOnlyList<string> sensitiveValues,
        bool printOutputOnSuccess = true)
    {
        var commandForLog = BuildCommandForLog(fileName, arguments, sensitiveValues);
        Console.WriteLine($"> {commandForLog}");

        if (whatIf)
        {
            return ProcessResult.Ok();
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            processStartInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode == 0)
        {
            if (printOutputOnSuccess)
            {
                if (!string.IsNullOrWhiteSpace(stdOut))
                {
                    Console.Write(MaskSensitive(stdOut, sensitiveValues));
                }

                if (!string.IsNullOrWhiteSpace(stdErr))
                {
                    Console.Error.Write(MaskSensitive(stdErr, sensitiveValues));
                }
            }

            return ProcessResult.Ok();
        }

        if (!string.IsNullOrWhiteSpace(stdOut))
        {
            Console.Write(MaskSensitive(stdOut, sensitiveValues));
        }

        if (!string.IsNullOrWhiteSpace(stdErr))
        {
            Console.Error.Write(MaskSensitive(stdErr, sensitiveValues));
        }

        return ProcessResult.Fail($"Command failed with exit code {process.ExitCode}: {fileName}");
    }

    private static string BuildCommandForLog(string fileName, IReadOnlyList<string> args, IReadOnlyList<string> sensitiveValues)
    {
        var command = fileName + " " + string.Join(" ", args.Select(QuoteArgForLog));
        return MaskSensitive(command, sensitiveValues);
    }

    private static string QuoteArgForLog(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        return arg;
    }

    private static string MaskSensitive(string text, IReadOnlyList<string> sensitiveValues)
    {
        var value = text;
        foreach (var secret in sensitiveValues.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            value = value.Replace(secret, "***", StringComparison.Ordinal);
        }

        return value;
    }
}
