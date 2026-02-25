internal sealed record CliOptions(
    string RootPath,
    bool Push,
    string? Source,
    string Configuration,
    string? OutputFolderOverride,
    bool SkipDuplicateRequested,
    bool WhatIf,
    bool Yes,
    IReadOnlyList<string> IncludeGlobs,
    IReadOnlyList<string> ExcludeGlobs)
{
    public static ParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return ParseResult.Fail("Missing rootPath.");
        }

        var rootPath = args[0].Trim();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return ParseResult.Fail("rootPath is empty.");
        }

        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath))
        {
            return ParseResult.Fail($"rootPath does not exist: {rootPath}");
        }

        var push = false;
        string? source = null;
        var configuration = "Release";
        string? output = null;
        var skipDuplicate = false;
        var whatIf = false;
        var yes = false;
        var includes = new List<string>();
        var excludes = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-push":
                    push = true;
                    break;
                case "-source":
                    i++;
                    if (i >= args.Length)
                    {
                        return ParseResult.Fail("-source requires a value.");
                    }

                    source = args[i];
                    break;
                case "-configuration":
                    i++;
                    if (i >= args.Length)
                    {
                        return ParseResult.Fail("-configuration requires a value.");
                    }

                    configuration = args[i];
                    if (!string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase))
                    {
                        return ParseResult.Fail("-configuration must be Release or Debug.");
                    }

                    configuration = char.ToUpperInvariant(configuration[0]) + configuration[1..].ToLowerInvariant();
                    break;
                case "-output":
                    i++;
                    if (i >= args.Length)
                    {
                        return ParseResult.Fail("-output requires a value.");
                    }

                    output = args[i];
                    break;
                case "-skip-duplicate":
                    skipDuplicate = true;
                    break;
                case "-whatif":
                    whatIf = true;
                    break;
                case "-yes":
                    yes = true;
                    break;
                case "-include":
                    i++;
                    if (i >= args.Length)
                    {
                        return ParseResult.Fail("-include requires a value.");
                    }

                    includes.Add(args[i]);
                    break;
                case "-exclude":
                    i++;
                    if (i >= args.Length)
                    {
                        return ParseResult.Fail("-exclude requires a value.");
                    }

                    excludes.Add(args[i]);
                    break;
                default:
                    return ParseResult.Fail($"Unknown argument: {arg}");
            }
        }

        var options = new CliOptions(
            RootPath: rootPath,
            Push: push,
            Source: source,
            Configuration: configuration,
            OutputFolderOverride: output,
            SkipDuplicateRequested: skipDuplicate,
            WhatIf: whatIf,
            Yes: yes,
            IncludeGlobs: includes,
            ExcludeGlobs: excludes);

        return ParseResult.Ok(options);
    }
}

internal sealed record ParseResult(bool Success, CliOptions? Options, string? Error)
{
    public static ParseResult Ok(CliOptions options) => new(true, options, null);
    public static ParseResult Fail(string error) => new(false, null, error);
}
