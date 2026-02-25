using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

internal static class AutoBumpService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static AutoBumpResult Apply(
        string rootPath,
        IReadOnlyDictionary<string, ProjectInfo> allProjects,
        IReadOnlyList<ProjectInfo> packageProjects,
        string bumpLevel,
        bool whatIf)
    {
        var stateResult = LoadState();
        if (!stateResult.Success)
        {
            return AutoBumpResult.Fail(stateResult.Error!);
        }

        var state = stateResult.State!;
        var fingerprintByPackagePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packageProjects)
        {
            var fingerprintResult = ComputePackageFingerprint(rootPath, package, allProjects);
            if (!fingerprintResult.Success)
            {
                return AutoBumpResult.Fail(fingerprintResult.Error!);
            }

            fingerprintByPackagePath[package.Path] = fingerprintResult.Fingerprint!;
        }

        var packageByPath = packageProjects.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var packageById = packageProjects
            .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Version, NugetVersionComparer.Instance).First(), StringComparer.OrdinalIgnoreCase);

        var directChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packageProjects)
        {
            if (!state.Packages.TryGetValue(package.Path, out var existing) ||
                !string.Equals(existing.Fingerprint, fingerprintByPackagePath[package.Path], StringComparison.Ordinal) ||
                !string.Equals(existing.Version, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                directChanged.Add(package.Path);
            }
        }

        var dependents = BuildDependents(packageProjects, allProjects, packageById);
        var allChanged = new HashSet<string>(directChanged, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(directChanged);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!dependents.TryGetValue(current, out var parentList))
            {
                continue;
            }

            foreach (var parent in parentList)
            {
                if (allChanged.Add(parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }

        var bumpedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packagePath in allChanged.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var package = packageByPath[packagePath];
            var bump = VersionBumper.Bump(package.Version, bumpLevel);
            if (!bump.Success)
            {
                return AutoBumpResult.Fail($"Failed bumping {package.PackageId}: {bump.Error}");
            }

            bumpedVersions[packagePath] = bump.Version!;
        }

        if (!whatIf)
        {
            foreach (var packagePath in bumpedVersions.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var updateResult = UpdateProjectVersion(packagePath, bumpedVersions[packagePath]);
                if (!updateResult.Success)
                {
                    return AutoBumpResult.Fail(updateResult.Error!);
                }
            }
        }

        return AutoBumpResult.Ok(bumpedVersions);
    }

    public static ChangedPackagesResult DetectChangedPackages(
        string rootPath,
        IReadOnlyDictionary<string, ProjectInfo> allProjects,
        IReadOnlyList<ProjectInfo> packageProjects)
    {
        var stateResult = LoadState();
        if (!stateResult.Success)
        {
            return ChangedPackagesResult.Fail(stateResult.Error!);
        }

        var state = stateResult.State!;
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packageProjects)
        {
            var fingerprintResult = ComputePackageFingerprint(rootPath, package, allProjects);
            if (!fingerprintResult.Success)
            {
                return ChangedPackagesResult.Fail(fingerprintResult.Error!);
            }

            if (!state.Packages.TryGetValue(package.Path, out var existing) ||
                !string.Equals(existing.Fingerprint, fingerprintResult.Fingerprint, StringComparison.Ordinal) ||
                !string.Equals(existing.Version, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(package.Path);
            }
        }

        return ChangedPackagesResult.Ok(changed);
    }

    public static StateUpdateResult SaveState(
        string rootPath,
        IReadOnlyDictionary<string, ProjectInfo> allProjects,
        IReadOnlyList<ProjectInfo> packageProjects)
    {
        var loadResult = LoadState();
        if (!loadResult.Success)
        {
            return StateUpdateResult.Fail(loadResult.Error!);
        }

        var state = loadResult.State!;
        foreach (var package in packageProjects)
        {
            var fingerprintResult = ComputePackageFingerprint(rootPath, package, allProjects);
            if (!fingerprintResult.Success)
            {
                return StateUpdateResult.Fail(fingerprintResult.Error!);
            }

            state.Packages[package.Path] = new PackageStateEntry
            {
                Version = package.Version,
                Fingerprint = fingerprintResult.Fingerprint!,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        var saveResult = SaveState(state);
        return !saveResult.Success ? StateUpdateResult.Fail(saveResult.Error!) : StateUpdateResult.Ok();
    }

    private static Dictionary<string, List<string>> BuildDependents(
        IReadOnlyList<ProjectInfo> packageProjects,
        IReadOnlyDictionary<string, ProjectInfo> allProjects,
        IReadOnlyDictionary<string, ProjectInfo> packageById)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packageProjects)
        {
            var dependencyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var projectRef in package.ProjectReferences)
            {
                if (allProjects.TryGetValue(projectRef, out var referenced) && referenced.IsPackage)
                {
                    dependencyPaths.Add(referenced.Path);
                }
            }

            foreach (var packageRef in package.PackageReferences)
            {
                if (packageById.TryGetValue(packageRef.Id, out var referencedPackage))
                {
                    dependencyPaths.Add(referencedPackage.Path);
                }
            }

            foreach (var dependencyPath in dependencyPaths)
            {
                if (!dependents.TryGetValue(dependencyPath, out var list))
                {
                    list = [];
                    dependents[dependencyPath] = list;
                }

                list.Add(package.Path);
            }
        }

        return dependents;
    }

    private static UpdateVersionResult UpdateProjectVersion(string projectPath, string newVersion)
    {
        try
        {
            var doc = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            var versionElement = doc
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Version");

            if (versionElement is not null)
            {
                versionElement.Value = newVersion;
            }
            else
            {
                var root = doc.Root;
                if (root is null)
                {
                    return UpdateVersionResult.Fail($"Invalid project XML: {projectPath}");
                }

                var propertyGroup = root.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup");
                if (propertyGroup is null)
                {
                    propertyGroup = new XElement(root.GetDefaultNamespace() + "PropertyGroup");
                    root.AddFirst(propertyGroup);
                }

                propertyGroup.Add(new XElement(root.GetDefaultNamespace() + "Version", newVersion));
            }

            doc.Save(projectPath);
            return UpdateVersionResult.Ok();
        }
        catch (Exception ex)
        {
            return UpdateVersionResult.Fail($"Failed updating version in '{projectPath}': {ex.Message}");
        }
    }

    private static FingerprintResult ComputePackageFingerprint(
        string rootPath,
        ProjectInfo package,
        IReadOnlyDictionary<string, ProjectInfo> allProjects)
    {
        try
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetDirectoryName(package.Path)!
            };
            var explicitFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var visitedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(package.ProjectReferences.Reverse());
            while (stack.Count > 0)
            {
                var projectPath = stack.Pop();
                if (!visitedRefs.Add(projectPath))
                {
                    continue;
                }

                if (!allProjects.TryGetValue(projectPath, out var referenced))
                {
                    continue;
                }

                if (!referenced.IsPackage)
                {
                    directories.Add(Path.GetDirectoryName(referenced.Path)!);
                }

                foreach (var next in referenced.ProjectReferences)
                {
                    stack.Push(next);
                }
            }

            foreach (var propsPath in EnumerateDirectoryBuildProps(package.Path))
            {
                explicitFiles.Add(propsPath);
            }

            var files = new List<string>();
            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    if (ShouldSkipFile(file, rootPath))
                    {
                        continue;
                    }

                    files.Add(file);
                }
            }

            foreach (var explicitFile in explicitFiles)
            {
                if (ShouldSkipFile(explicitFile, rootPath))
                {
                    continue;
                }

                files.Add(explicitFile);
            }

            files = files.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var sha = SHA256.Create();
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                var header = Encoding.UTF8.GetBytes(relative + "\n");
                sha.TransformBlock(header, 0, header.Length, null, 0);

                var content = File.ReadAllBytes(file);
                var contentHash = SHA256.HashData(content);
                sha.TransformBlock(contentHash, 0, contentHash.Length, null, 0);
            }

            sha.TransformFinalBlock([], 0, 0);
            var fingerprint = Convert.ToHexString(sha.Hash!);
            return FingerprintResult.Ok(fingerprint);
        }
        catch (Exception ex)
        {
            return FingerprintResult.Fail($"Failed computing fingerprint for {package.PackageId}: {ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateDirectoryBuildProps(string projectPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var props = Path.Combine(current.FullName, "Directory.Build.props");
            if (File.Exists(props))
            {
                yield return props;
            }

            var targets = Path.Combine(current.FullName, "Directory.Build.targets");
            if (File.Exists(targets))
            {
                yield return targets;
            }
        }
    }

    private static bool ShouldSkipFile(string filePath, string rootPath)
    {
        var rel = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
        if (rel.StartsWith("../", StringComparison.Ordinal) || rel.StartsWith("..", StringComparison.Ordinal))
        {
            rel = filePath.Replace('\\', '/');
        }

        if (rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            rel.Contains("/.vs/", StringComparison.OrdinalIgnoreCase) ||
            rel.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
            rel.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(filePath);
        return ext.Equals(".nupkg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".snupkg", StringComparison.OrdinalIgnoreCase);
    }

    private static StateLoadResult LoadState()
    {
        try
        {
            var path = GetStatePath();
            if (!File.Exists(path))
            {
                return StateLoadResult.Ok(new NugetUtilState());
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<NugetUtilState>(json, JsonOptions) ?? new NugetUtilState();
            return StateLoadResult.Ok(state);
        }
        catch (Exception ex)
        {
            return StateLoadResult.Fail($"Failed loading state: {ex.Message}");
        }
    }

    private static StateSaveResult SaveState(NugetUtilState state)
    {
        try
        {
            var path = GetStatePath();
            var folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(false));
            return StateSaveResult.Ok();
        }
        catch (Exception ex)
        {
            return StateSaveResult.Fail($"Failed saving state: {ex.Message}");
        }
    }

    private static string GetStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "NugetUtil", "state.json");
    }
}

internal static class VersionBumper
{
    public static BumpVersionResult Bump(string version, string level)
    {
        var core = version.Split('-', 2)[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (core.Length < 3 || !int.TryParse(core[0], out var major) || !int.TryParse(core[1], out var minor) || !int.TryParse(core[2], out var patch))
        {
            return BumpVersionResult.Fail($"Version '{version}' is not supported for auto bump. Expected semantic version with major.minor.patch.");
        }

        switch (level)
        {
            case "major":
                major++;
                minor = 0;
                patch = 0;
                break;
            case "minor":
                minor++;
                patch = 0;
                break;
            default:
                patch++;
                break;
        }

        return BumpVersionResult.Ok($"{major}.{minor}.{patch}");
    }
}

internal sealed class NugetUtilState
{
    public Dictionary<string, PackageStateEntry> Packages { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PackageStateEntry
{
    public string Version { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; init; }
}

internal sealed record AutoBumpResult(bool Success, IReadOnlyDictionary<string, string>? BumpedVersions, string? Error)
{
    public static AutoBumpResult Ok(IReadOnlyDictionary<string, string> bumpedVersions) => new(true, bumpedVersions, null);
    public static AutoBumpResult Fail(string error) => new(false, null, error);
}

internal sealed record ChangedPackagesResult(bool Success, IReadOnlySet<string>? PackagePaths, string? Error)
{
    public static ChangedPackagesResult Ok(IReadOnlySet<string> packagePaths) => new(true, packagePaths, null);
    public static ChangedPackagesResult Fail(string error) => new(false, null, error);
}

internal sealed record BumpVersionResult(bool Success, string? Version, string? Error)
{
    public static BumpVersionResult Ok(string version) => new(true, version, null);
    public static BumpVersionResult Fail(string error) => new(false, null, error);
}

internal sealed record UpdateVersionResult(bool Success, string? Error)
{
    public static UpdateVersionResult Ok() => new(true, null);
    public static UpdateVersionResult Fail(string error) => new(false, error);
}

internal sealed record FingerprintResult(bool Success, string? Fingerprint, string? Error)
{
    public static FingerprintResult Ok(string fingerprint) => new(true, fingerprint, null);
    public static FingerprintResult Fail(string error) => new(false, null, error);
}

internal sealed record StateLoadResult(bool Success, NugetUtilState? State, string? Error)
{
    public static StateLoadResult Ok(NugetUtilState state) => new(true, state, null);
    public static StateLoadResult Fail(string error) => new(false, null, error);
}

internal sealed record StateSaveResult(bool Success, string? Error)
{
    public static StateSaveResult Ok() => new(true, null);
    public static StateSaveResult Fail(string error) => new(false, error);
}

internal sealed record StateUpdateResult(bool Success, string? Error)
{
    public static StateUpdateResult Ok() => new(true, null);
    public static StateUpdateResult Fail(string error) => new(false, error);
}
