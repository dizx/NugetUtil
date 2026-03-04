internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var exitCode = await NugetUtilProgram.RunAsync(args);
            if (!IsUsageInvocation(args))
            {
                PrintExitMessage(exitCode);
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.InvalidArgsOrConfig;
        }
    }

    private static bool IsUsageInvocation(string[] args)
    {
        return args.Any(a => string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "/?", StringComparison.OrdinalIgnoreCase));
    }

    private static void PrintExitMessage(int exitCode)
    {
        if (exitCode == ExitCodes.Success)
        {
            Console.WriteLine("Job finished successfully.");
            return;
        }

        Console.Error.WriteLine($"Job failed with exit code: {exitCode}");
    }
}
