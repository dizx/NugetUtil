internal static class NugetUtilProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var parse = CliOptions.Parse(args);
            if (!parse.Success)
            {
                Console.Error.WriteLine(parse.Error);
                PrintUsage();
                return ExitCodes.InvalidArgsOrConfig;
            }

            var options = parse.Options!;
            Console.WriteLine($"Root path: {options.RootPath}");

            var configResult = ConfigLoader.Load();
            if (!configResult.Success)
            {
                Console.Error.WriteLine(configResult.Error);
                return ExitCodes.InvalidArgsOrConfig;
            }

            var config = configResult.Config!;
            var discovery = ProjectDiscovery.Discover(options, config);
            if (!discovery.Success)
            {
                Console.Error.WriteLine(discovery.Error);
                return ExitCodes.InvalidArgsOrConfig;
            }

            var allProjects = discovery.Projects!;
            var packageProjects = allProjects.Values.Where(p => p.IsPackage).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var latestPackageVersions = packageProjects
                .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(p => p.Version).OrderByDescending(v => v, NugetVersionComparer.Instance).First(),
                    StringComparer.OrdinalIgnoreCase);

            if (packageProjects.Count == 0)
            {
                Console.WriteLine("No package projects found.");
                return ExitCodes.Success;
            }

            Console.WriteLine("Discovered packages:");
            foreach (var package in packageProjects)
            {
                Console.WriteLine($"- {package.PackageId} ({package.Path})");
            }

            var configuredOutput = string.IsNullOrWhiteSpace(options.OutputFolderOverride)
                ? (config.Behavior.OutputFolder ?? "artifacts\\nuget")
                : options.OutputFolderOverride;
            var effectiveSkipDuplicate = options.SkipDuplicateRequested || (config.Behavior.SkipDuplicate ?? true);

            var outputFolder = Path.IsPathRooted(configuredOutput)
                ? configuredOutput
                : Path.GetFullPath(Path.Combine(options.RootPath, configuredOutput));

            Directory.CreateDirectory(outputFolder);

            var createdPackages = new List<string>();

            foreach (var package in packageProjects)
            {
                Console.WriteLine();
                Console.WriteLine($"Processing package: {package.PackageId}");
                Console.WriteLine($"- Version: {package.Version}");
                Console.WriteLine($"- TFM: {package.TargetFramework}");

                var directReferencedProjects = package.ProjectReferences
                    .Select(pr => allProjects.TryGetValue(pr, out var found) ? found : null)
                    .Where(p => p is not null)
                    .Cast<ProjectInfo>()
                    .ToList();

                var nonPackableRefs = directReferencedProjects.Where(p => !p.IsPackage).ToList();
                if (nonPackableRefs.Count > 0)
                {
                    Console.WriteLine($"- Nuspec mode required: references non-packable: {string.Join(", ", nonPackableRefs.Select(r => r.ProjectName))}");
                }
                else
                {
                    Console.WriteLine("- Nuspec mode not required by references, using nuspec mode by default.");
                }

                var buildResult = await ProcessRunner.RunAsync(
                    fileName: "dotnet",
                    arguments: ["build", package.Path, "-c", options.Configuration],
                    workingDirectory: options.RootPath,
                    whatIf: options.WhatIf,
                    sensitiveValues: [],
                    printOutputOnSuccess: options.VerboseBuildOutput);

                if (!buildResult.Success)
                {
                    return ExitCodes.BuildFailed;
                }

                if (!options.VerboseBuildOutput)
                {
                    Console.WriteLine("- Build: succeeded");
                }

                var dependencyResult = NuspecGenerator.BuildDependencies(package, allProjects, latestPackageVersions);
                if (!dependencyResult.Success)
                {
                    Console.Error.WriteLine(dependencyResult.Error);
                    return ExitCodes.InvalidArgsOrConfig;
                }

                var nuspecPath = Path.Combine(Path.GetDirectoryName(package.Path)!, $"{package.PackageId}.nuspec");
                var nuspecWriteResult = NuspecGenerator.WriteNuspec(
                    nuspecPath,
                    package,
                    dependencyResult.Dependencies!,
                    nonPackableRefs,
                    options.Configuration);

                if (!nuspecWriteResult.Success)
                {
                    Console.Error.WriteLine(nuspecWriteResult.Error);
                    return ExitCodes.PackFailed;
                }

                Console.WriteLine($"- Nuspec: {nuspecPath}");

                var packResult = await ProcessRunner.RunAsync(
                    fileName: "dotnet",
                    arguments:
                    [
                        "pack",
                        nuspecPath,
                        "-o",
                        outputFolder
                    ],
                    workingDirectory: options.RootPath,
                    whatIf: options.WhatIf,
                    sensitiveValues: []);

                if (!packResult.Success)
                {
                    return ExitCodes.PackFailed;
                }

                var nupkgPath = Path.Combine(outputFolder, $"{package.PackageId}.{package.Version}.nupkg");
                if (!options.WhatIf && !File.Exists(nupkgPath))
                {
                    Console.Error.WriteLine($"Pack succeeded but expected file not found: {nupkgPath}");
                    return ExitCodes.PackFailed;
                }

                Console.WriteLine($"- Packed: {nupkgPath}");
                createdPackages.Add(nupkgPath);
            }

            if (!options.Push)
            {
                return ExitCodes.Success;
            }

            var sourceName = string.IsNullOrWhiteSpace(options.Source) ? config.DefaultSource : options.Source;
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                Console.Error.WriteLine("No source provided and no defaultSource set in config.");
                return ExitCodes.InvalidArgsOrConfig;
            }

            if (!config.Sources.TryGetValue(sourceName, out var sourceConfig))
            {
                Console.Error.WriteLine($"Source '{sourceName}' not found in config.");
                return ExitCodes.InvalidArgsOrConfig;
            }

            if (string.IsNullOrWhiteSpace(sourceConfig.ApiKey))
            {
                Console.Error.WriteLine($"Source '{sourceName}' must define apiKey.");
                return ExitCodes.InvalidArgsOrConfig;
            }

            Console.WriteLine();
            Console.WriteLine($"Push source: {sourceName}");

            if (!options.Yes && !options.WhatIf)
            {
                Console.Write($"Push {createdPackages.Count} package(s)? [y/N]: ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Push cancelled.");
                    return ExitCodes.Success;
                }
            }

            foreach (var nupkg in createdPackages)
            {
                var pushArgs = new List<string>
                {
                    "push",
                    nupkg,
                    "--source",
                    sourceName,
                    "--api-key",
                    sourceConfig.ApiKey,
                    "--interactive"
                };

                if (effectiveSkipDuplicate)
                {
                    pushArgs.Add("--skip-duplicate");
                }

                var pushResult = await ProcessRunner.RunAsync(
                    fileName: "dotnet",
                    arguments: ["nuget", .. pushArgs],
                    workingDirectory: options.RootPath,
                    whatIf: options.WhatIf,
                    sensitiveValues: [sourceConfig.ApiKey]);

                if (!pushResult.Success)
                {
                    return ExitCodes.PushFailed;
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.InvalidArgsOrConfig;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: nugetutil \"<path>\" [options]");
        Console.WriteLine("  <path> = repository root path containing .csproj files");
        Console.WriteLine("Options:");
        Console.WriteLine("  -push");
        Console.WriteLine("  -source \"<name>\"   (NuGet source name, e.g. \"MyFeed\")");
        Console.WriteLine("  -configuration Release|Debug");
        Console.WriteLine("  -output \"<folder>\"");
        Console.WriteLine("  -skip-duplicate");
        Console.WriteLine("  -verbose-build");
        Console.WriteLine("  -whatif");
        Console.WriteLine("  -yes");
        Console.WriteLine("  -include \"<glob>\" (repeatable)");
        Console.WriteLine("  -exclude \"<glob>\" (repeatable)");
    }
}
