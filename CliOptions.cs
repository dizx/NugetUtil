internal sealed record CliOptions(
    string RootPath,
    string? DeployablePackagePath,
    bool SaveAxNuspec,
    bool Push,
    string? Source,
    string Configuration,
    string? OutputFolderOverride,
    bool SkipDuplicateRequested,
    bool VerboseBuildOutput,
    bool Force,
    bool AutoBump,
    string BumpLevel,
    bool WhatIf,
    bool Yes,
    IReadOnlyList<string> IncludeGlobs,
    IReadOnlyList<string> ExcludeGlobs)
{
    public static ParseResult Parse(string[] args)
    {
        var optionStartIndex = 0;
        var rootPath = Directory.GetCurrentDirectory();
        var rootPathProvided = false;

        if (args.Length > 0 && !IsOption(args[0]))
        {
            rootPath = args[0].Trim();
            optionStartIndex = 1;
            rootPathProvided = true;
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return ParseResult.Fail("rootPath is empty.");
            }
        }

        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath))
        {
            return ParseResult.Fail($"rootPath does not exist: {rootPath}");
        }

        var push = false;
        string? deployablePackagePath = null;
        var saveAxNuspec = false;
        string? source = null;
        var configuration = "Release";
        string? output = null;
        var skipDuplicate = false;
        var verboseBuildOutput = false;
        var force = false;
        var autoBump = false;
        var bumpLevel = "patch";
        var whatIf = false;
        var yes = false;
        var includes = new List<string>();
        var excludes = new List<string>();

        for (var i = optionStartIndex; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-push":
                    push = true;
                    break;
                case "-deployable-package":
                    i++;
                    if (i >= args.Length)
                    {
                        return ParseResult.Fail("-deployable-package requires a value.");
                    }

                    deployablePackagePath = Path.GetFullPath(args[i]);
                    break;
                case "-save-nuspec":
                    saveAxNuspec = true;
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
                case "-verbose-build":
                    verboseBuildOutput = true;
                    break;
                case "-force":
                    force = true;
                    break;
                case "-autobump":
                    autoBump = true;
                    break;
                case "-bumplevel":
                    i++;
                    if (i >= args.Length)
                    {
                        return ParseResult.Fail("-bumplevel requires a value.");
                    }

                    bumpLevel = args[i].Trim().ToLowerInvariant();
                    if (bumpLevel is not ("patch" or "minor" or "major"))
                    {
                        return ParseResult.Fail("-bumplevel must be patch, minor, or major.");
                    }
                    break;
                case "-dryrun":
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

        if (!string.IsNullOrWhiteSpace(deployablePackagePath) && !File.Exists(deployablePackagePath))
        {
            return ParseResult.Fail($"Deployable package does not exist: {deployablePackagePath}");
        }

        if (!rootPathProvided && string.IsNullOrWhiteSpace(deployablePackagePath))
        {
            var rootOfRootPath = Path.GetPathRoot(rootPath);
            if (!string.IsNullOrWhiteSpace(rootOfRootPath) &&
                string.Equals(rootPath.TrimEnd(Path.DirectorySeparatorChar), rootOfRootPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return ParseResult.Fail("Current directory is a drive root. Run nugetutil from a project/solution folder or pass an explicit path.");
            }

            if (!LooksLikeSourcePath(rootPath))
            {
                return ParseResult.Fail(
                    "Current directory does not look like a source/solution path. Run nugetutil from a folder containing .sln/.csproj (or subfolders with .sln/.csproj), or pass an explicit path.");
            }
        }

        var options = new CliOptions(
            RootPath: rootPath,
            DeployablePackagePath: deployablePackagePath,
            SaveAxNuspec: saveAxNuspec,
            Push: push,
            Source: source,
            Configuration: configuration,
            OutputFolderOverride: output,
            SkipDuplicateRequested: skipDuplicate,
            VerboseBuildOutput: verboseBuildOutput,
            Force: force,
            AutoBump: autoBump,
            BumpLevel: bumpLevel,
            WhatIf: whatIf,
            Yes: yes,
            IncludeGlobs: includes,
            ExcludeGlobs: excludes);

        return ParseResult.Ok(options);
    }

    private static bool IsOption(string value)
        => value.StartsWith("-", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal);

    private static bool LooksLikeSourcePath(string path)
    {
        if (Directory.Exists(Path.Combine(path, ".git")) ||
            File.Exists(Path.Combine(path, "Directory.Build.props")) ||
            File.Exists(Path.Combine(path, "Directory.Build.targets")))
        {
            return true;
        }

        var hasSln = HasPatternInTopDirectory(path, "*.sln");
        if (hasSln)
        {
            return true;
        }

        var hasTopLevelCsproj = HasPatternInTopDirectory(path, "*.csproj");
        if (hasTopLevelCsproj)
        {
            return true;
        }

        return HasSolutionOrProjectWithinDepth(path, maxDepth: 3, maxDirectoriesToInspect: 300);
    }

    private static bool HasSolutionOrProjectWithinDepth(string rootPath, int maxDepth, int maxDirectoriesToInspect)
    {
        var inspected = 0;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));

        while (queue.Count > 0 && inspected < maxDirectoriesToInspect)
        {
            var (path, depth) = queue.Dequeue();
            inspected++;

            if (HasPatternInTopDirectory(path, "*.sln") ||
                HasPatternInTopDirectory(path, "*.csproj"))
            {
                return true;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(path);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = System.IO.Path.GetFileName(child);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                queue.Enqueue((child, depth + 1));
            }
        }

        return false;
    }

    private static bool HasPatternInTopDirectory(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }
}

internal sealed record ParseResult(bool Success, CliOptions? Options, string? Error)
{
    public static ParseResult Ok(CliOptions options) => new(true, options, null);
    public static ParseResult Fail(string error) => new(false, null, error);
}
