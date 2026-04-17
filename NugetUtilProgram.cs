using System.Reflection;

internal static class NugetUtilProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Any(a => string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "/?", StringComparison.OrdinalIgnoreCase)))
            {
                PrintUsage();
                return ExitCodes.Success;
            }

            if (args.Any(a => string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"NugetUtil {GetToolVersion()}");
                return ExitCodes.Success;
            }

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

            var configuredOutput = string.IsNullOrWhiteSpace(options.OutputFolderOverride)
                ? (config.Behavior.OutputFolder ?? "artifacts\\nuget")
                : options.OutputFolderOverride;
            var effectiveSkipDuplicate = options.SkipDuplicateRequested || (config.Behavior.SkipDuplicate ?? true);

            var outputFolder = Path.IsPathRooted(configuredOutput)
                ? configuredOutput
                : Path.GetFullPath(Path.Combine(options.RootPath, configuredOutput));

            Directory.CreateDirectory(outputFolder);

            if (!string.IsNullOrWhiteSpace(options.DeployablePackagePath))
            {
                Console.WriteLine($"Dynamics 365 FO deployable package mode: {options.DeployablePackagePath}");

                var foPackResult = await FoDeployablePackageService.BuildAsync(
                    packageSourcePath: options.DeployablePackagePath,
                    outputFolder: outputFolder,
                    workingDirectory: options.RootPath,
                    saveNuspecToOutput: options.SaveFoNuspec,
                    whatIf: options.WhatIf);

                if (!foPackResult.Success)
                {
                    Console.Error.WriteLine(foPackResult.Error);
                    return ExitCodes.PackFailed;
                }

                Console.WriteLine($"- PackageId: {foPackResult.PackageId}");
                Console.WriteLine($"- Version: {foPackResult.Version}");
                Console.WriteLine($"- Packed: {foPackResult.NupkgPath}");
                if (!string.IsNullOrWhiteSpace(foPackResult.ExportedNuspecPath))
                {
                    Console.WriteLine($"- Nuspec: {foPackResult.ExportedNuspecPath}");
                }

                if (!options.Push)
                {
                    return ExitCodes.Success;
                }

                var foSourceName = string.IsNullOrWhiteSpace(options.Source) ? config.DefaultSource : options.Source;
                if (string.IsNullOrWhiteSpace(foSourceName))
                {
                    Console.Error.WriteLine("No source provided and no defaultSource set in config.");
                    return ExitCodes.InvalidArgsOrConfig;
                }

                if (!config.Sources.TryGetValue(foSourceName, out var foSourceConfig))
                {
                    Console.Error.WriteLine($"Source '{foSourceName}' not found in config.");
                    return ExitCodes.InvalidArgsOrConfig;
                }

                if (string.IsNullOrWhiteSpace(foSourceConfig.ApiKey))
                {
                    Console.Error.WriteLine($"Source '{foSourceName}' must define apiKey.");
                    return ExitCodes.InvalidArgsOrConfig;
                }

                Console.WriteLine();
                Console.WriteLine($"Push source: {foSourceName}");

                var foNupkgPath = foPackResult.NupkgPath;
                if (string.IsNullOrWhiteSpace(foNupkgPath))
                {
                    Console.Error.WriteLine("Dynamics 365 FO package path is empty after packing.");
                    return ExitCodes.PackFailed;
                }

                var pushArgs = new List<string>
                {
                    "push",
                    foNupkgPath,
                    "--source",
                    foSourceName,
                    "--api-key",
                    foSourceConfig.ApiKey,
                    "--interactive"
                };

                if (effectiveSkipDuplicate)
                {
                    pushArgs.Add("--skip-duplicate");
                }

                if (!options.Yes && !options.WhatIf)
                {
                    Console.Write("Push 1 package(s)? [y/N]: ");
                    var answer = Console.ReadLine();
                    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Push cancelled.");
                        return ExitCodes.Success;
                    }
                }

                var pushResult = await ProcessRunner.RunAsync(
                    fileName: "dotnet",
                    arguments: ["nuget", .. pushArgs],
                    workingDirectory: options.RootPath,
                    whatIf: options.WhatIf,
                    sensitiveValues: [foSourceConfig.ApiKey]);

                if (!pushResult.Success)
                {
                    return ExitCodes.PushFailed;
                }

                return ExitCodes.Success;
            }

            var discovery = ProjectDiscovery.Discover(options, config);
            if (!discovery.Success)
            {
                Console.Error.WriteLine(discovery.Error);
                return ExitCodes.InvalidArgsOrConfig;
            }

            var allProjects = discovery.Projects!;
            var packageProjects = allProjects.Values.Where(p => p.IsPackage).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();

            if (packageProjects.Count == 0)
            {
                Console.WriteLine("No package projects found.");
                return ExitCodes.Success;
            }

            HashSet<string>? selectedPackagePathSet = null;
            if (options.AutoBump)
            {
                if (options.Force)
                {
                    Console.WriteLine("Auto bump force mode enabled: bumping all discovered packages.");
                }

                var autoBumpResult = AutoBumpService.Apply(
                    rootPath: options.RootPath,
                    allProjects: allProjects,
                    packageProjects: packageProjects,
                    bumpLevel: options.BumpLevel,
                    forceAll: options.Force,
                    whatIf: options.WhatIf);

                if (!autoBumpResult.Success)
                {
                    Console.Error.WriteLine(autoBumpResult.Error);
                    return ExitCodes.InvalidArgsOrConfig;
                }

                var bumped = autoBumpResult.BumpedVersions!;
                if (bumped.Count == 0)
                {
                    Console.WriteLine("No package updates detected.");
                    return ExitCodes.Success;
                }

                Console.WriteLine("Auto bump packages:");
                foreach (var package in packageProjects.Where(p => bumped.ContainsKey(p.Path)).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"- {package.PackageId}: {package.Version} -> {bumped[package.Path]}");
                }

                selectedPackagePathSet = bumped.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!options.WhatIf)
                {
                    var refreshedDiscovery = ProjectDiscovery.Discover(options, config);
                    if (!refreshedDiscovery.Success)
                    {
                        Console.Error.WriteLine(refreshedDiscovery.Error);
                        return ExitCodes.InvalidArgsOrConfig;
                    }

                    allProjects = refreshedDiscovery.Projects!;
                    packageProjects = allProjects.Values.Where(p => p.IsPackage).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
                }
                else
                {
                    allProjects = allProjects.ToDictionary(
                        kv => kv.Key,
                        kv => bumped.TryGetValue(kv.Key, out var newVersion)
                            ? kv.Value with { Version = newVersion }
                            : kv.Value,
                        StringComparer.OrdinalIgnoreCase);

                    packageProjects = allProjects.Values.Where(p => p.IsPackage).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            else
            {
                if (options.Force)
                {
                    selectedPackagePathSet = packageProjects.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Console.WriteLine("Force mode enabled: processing all discovered packages.");
                }
                else
                {
                    var changedPackagesResult = AutoBumpService.DetectChangedPackages(options.RootPath, allProjects, packageProjects);
                    if (!changedPackagesResult.Success)
                    {
                        Console.Error.WriteLine(changedPackagesResult.Error);
                        return ExitCodes.InvalidArgsOrConfig;
                    }

                    var changedPaths = changedPackagesResult.PackagePaths!;
                    if (changedPaths.Count == 0)
                    {
                        Console.WriteLine("No package updates detected.");
                        if (!options.Push)
                        {
                            return ExitCodes.Success;
                        }

                        Console.WriteLine("Push requested: looking for existing packages in output folder.");
                        selectedPackagePathSet = [];
                    }
                    else
                    {
                        selectedPackagePathSet = changedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        Console.WriteLine("Changed packages:");
                        foreach (var package in packageProjects.Where(p => selectedPackagePathSet.Contains(p.Path)).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"- {package.PackageId} ({package.Version})");
                        }
                    }
                }
            }

            var discoveredPackageIds = packageProjects
                .Select(p => p.PackageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var latestPackageVersions = packageProjects
                .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(p => p.Version).OrderByDescending(v => v, NugetVersionComparer.Instance).First(),
                    StringComparer.OrdinalIgnoreCase);

            if (selectedPackagePathSet is not null)
            {
                packageProjects = packageProjects
                    .Where(p => selectedPackagePathSet.Contains(p.Path))
                    .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            Console.WriteLine("Discovered packages:");
            foreach (var package in packageProjects)
            {
                Console.WriteLine($"- {package.PackageId} ({package.Path})");
            }

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
                if (!options.WhatIf)
                {
                    var stateUpdateResult = AutoBumpService.SaveState(options.RootPath, allProjects, allProjects.Values.Where(p => p.IsPackage).ToList());
                    if (!stateUpdateResult.Success)
                    {
                        Console.Error.WriteLine(stateUpdateResult.Error);
                        return ExitCodes.InvalidArgsOrConfig;
                    }
                }

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

            var packagesToPush = new List<string>(createdPackages);
            if (packagesToPush.Count == 0)
            {
                var fallbackPackages = FindExistingPackagesToPush(outputFolder, discoveredPackageIds);
                packagesToPush.AddRange(fallbackPackages);
                if (packagesToPush.Count == 0)
                {
                    Console.WriteLine("No matching existing packages found to push.");
                    return ExitCodes.Success;
                }

                Console.WriteLine($"Using existing packages from output folder: {packagesToPush.Count}");
                foreach (var path in packagesToPush)
                {
                    Console.WriteLine($"- {path}");
                }
            }

            if (!options.Yes && !options.WhatIf)
            {
                Console.Write($"Push {packagesToPush.Count} package(s)? [y/N]: ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Push cancelled.");
                    return ExitCodes.Success;
                }
            }

            foreach (var nupkg in packagesToPush)
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

            if (!options.WhatIf)
            {
                var stateUpdateResult = AutoBumpService.SaveState(options.RootPath, allProjects, allProjects.Values.Where(p => p.IsPackage).ToList());
                if (!stateUpdateResult.Success)
                {
                    Console.Error.WriteLine(stateUpdateResult.Error);
                    return ExitCodes.InvalidArgsOrConfig;
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
        Console.WriteLine($"NugetUtil {GetToolVersion()}");
        Console.WriteLine("Usage: nugetutil [\"<path>\"] [options]");
        Console.WriteLine("   or:  nugetutil fopack \"<zip>\" [options] (Dynamics 365 FO deployable package zip)");
        Console.WriteLine("  <path> = optional repository root path (defaults to current directory)");
        Console.WriteLine("Options:");
        Console.WriteLine("  -save-nuspec   (with fopack or -deployable-package, save generated nuspec to output)");
        Console.WriteLine("  -push");
        Console.WriteLine("  -source \"<name>\"   (NuGet source name, e.g. \"MyFeed\")");
        Console.WriteLine("  -configuration Release|Debug");
        Console.WriteLine("  -output \"<folder>\"");
        Console.WriteLine("  -skip-duplicate");
        Console.WriteLine("  -verbose-build");
        Console.WriteLine("  -force");
        Console.WriteLine("  -autobump");
        Console.WriteLine("  -bumplevel patch|minor|major");
        Console.WriteLine("  -dryrun");
        Console.WriteLine("  -yes");
        Console.WriteLine("  -include \"<glob>\" (repeatable)");
        Console.WriteLine("  -exclude \"<glob>\" (repeatable)");
        Console.WriteLine("  -v|--version");
    }

    private static string GetToolVersion()
    {
        var assembly = typeof(NugetUtilProgram).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plusIndex = info.IndexOf('+');
            return plusIndex > 0 ? info[..plusIndex] : info;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static IReadOnlyList<string> FindExistingPackagesToPush(string outputFolder, IReadOnlySet<string> discoveredPackageIds)
    {
        if (!Directory.Exists(outputFolder) || discoveredPackageIds.Count == 0)
        {
            return [];
        }

        return Directory.EnumerateFiles(outputFolder, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                if (name.Contains(".symbols.", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return discoveredPackageIds.Any(id => name.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
