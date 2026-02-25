using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

internal static class NuspecGenerator
{
    private static readonly Regex PropertyPattern = new(@"\$\(([^)]+)\)", RegexOptions.Compiled);

    public static DependencyResult BuildDependencies(
        ProjectInfo package,
        IReadOnlyDictionary<string, ProjectInfo> allProjects,
        IReadOnlyDictionary<string, string> latestPackageVersions)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var propertyMapCache = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        var packageDepResult = AddDependenciesFromProject(package, latestPackageVersions, dependencies, unresolvedProperties, propertyMapCache);
        if (!packageDepResult.Success)
        {
            return packageDepResult;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(package.ProjectReferences.Reverse());
        while (stack.Count > 0)
        {
            var projectPath = stack.Pop();
            if (!visited.Add(projectPath))
            {
                continue;
            }

            if (!allProjects.TryGetValue(projectPath, out var referenced))
            {
                continue;
            }

            foreach (var next in referenced.ProjectReferences)
            {
                stack.Push(next);
            }

            if (referenced.IsPackage)
            {
                continue;
            }

            var refDepResult = AddDependenciesFromProject(referenced, latestPackageVersions, dependencies, unresolvedProperties, propertyMapCache);
            if (!refDepResult.Success)
            {
                return refDepResult;
            }
        }

        if (unresolvedProperties.Count > 0)
        {
            return DependencyResult.Fail($"Unresolved package version properties in {package.Path}: {string.Join(", ", unresolvedProperties.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");
        }

        var ordered = dependencies
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PackageRef(kv.Key, kv.Value))
            .ToList();

        return DependencyResult.Ok(ordered);
    }

    private static DependencyResult AddDependenciesFromProject(
        ProjectInfo project,
        IReadOnlyDictionary<string, string> latestPackageVersions,
        IDictionary<string, string> dependencies,
        ISet<string> unresolvedProperties,
        IDictionary<string, IReadOnlyDictionary<string, string>> propertyMapCache)
    {
        if (!propertyMapCache.TryGetValue(project.Path, out var projectProperties))
        {
            var propertyMapResult = BuildPropertyMap(project.Path);
            if (!propertyMapResult.Success)
            {
                return DependencyResult.Fail(propertyMapResult.Error!);
            }

            projectProperties = propertyMapResult.Properties!;
            propertyMapCache[project.Path] = projectProperties;
        }

        foreach (var packageRef in project.PackageReferences)
        {
            var id = packageRef.Id.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var version = ResolveProperties(packageRef.Version.Trim(), projectProperties, unresolvedProperties);
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (latestPackageVersions.TryGetValue(id, out var latestVersion))
            {
                version = latestVersion;
            }

            if (dependencies.TryGetValue(id, out var existing))
            {
                if (NugetVersionComparer.Instance.Compare(version, existing) > 0)
                {
                    dependencies[id] = version;
                }
            }
            else
            {
                dependencies[id] = version;
            }
        }

        return DependencyResult.Ok([]);
    }

    public static NuspecWriteResult WriteNuspec(
        string nuspecPath,
        ProjectInfo package,
        IReadOnlyList<PackageRef> dependencies,
        IReadOnlyList<ProjectInfo> directNonPackableReferences,
        string configuration)
    {
        try
        {
            var packageTfm = package.TargetFramework;
            var ns = XNamespace.Get("http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd");

            var metadata = new XElement(ns + "metadata",
                new XElement(ns + "id", package.PackageId),
                new XElement(ns + "version", package.Version),
                new XElement(ns + "title", string.IsNullOrWhiteSpace(package.Product) ? package.PackageId : package.Product),
                new XElement(ns + "authors", package.Authors),
                new XElement(ns + "owners", package.Company),
                new XElement(ns + "requireLicenseAcceptance", "false"),
                new XElement(ns + "description", package.Description),
                new XElement(ns + "tags", string.IsNullOrWhiteSpace(package.PackageTags) ? package.PackageId : package.PackageTags));

            if (!string.IsNullOrWhiteSpace(package.PackageReadmeFile))
            {
                metadata.Add(new XElement(ns + "readme", package.PackageReadmeFile));
            }

            var group = new XElement(ns + "group", new XAttribute("targetFramework", packageTfm));
            foreach (var dep in dependencies)
            {
                group.Add(new XElement(ns + "dependency",
                    new XAttribute("id", dep.Id),
                    new XAttribute("version", dep.Version)));
            }

            metadata.Add(new XElement(ns + "dependencies", group));

            var files = new XElement(ns + "files");

            var packageFile = new XElement(ns + "file",
                new XAttribute("src", $"bin\\{configuration}\\{package.TargetFramework}\\{package.ProjectName}.*"),
                new XAttribute("target", $"lib\\{package.TargetFramework}"));

            var packageExcludes = directNonPackableReferences
                .Select(r => $"bin\\{configuration}\\{package.TargetFramework}\\{r.ProjectName}.*")
                .ToList();

            if (packageExcludes.Count > 0)
            {
                packageFile.Add(new XAttribute("exclude", string.Join(";", packageExcludes)));
            }

            files.Add(packageFile);

            if (!string.IsNullOrWhiteSpace(package.PackageReadmeFile))
            {
                files.Add(new XElement(ns + "file",
                    new XAttribute("src", package.PackageReadmeFile),
                    new XAttribute("target", string.Empty)));
            }

            foreach (var referenced in directNonPackableReferences.OrderBy(p => p.ProjectName, StringComparer.OrdinalIgnoreCase))
            {
                var packageDir = Path.GetDirectoryName(package.Path)!;
                var referencedDir = Path.GetDirectoryName(referenced.Path)!;
                var relativeRefDir = Path.GetRelativePath(packageDir, referencedDir).Replace('/', '\\');
                var relativeOutputPath = relativeRefDir == "."
                    ? $"bin\\{configuration}\\{referenced.TargetFramework}\\{referenced.ProjectName}.*"
                    : $"{relativeRefDir}\\bin\\{configuration}\\{referenced.TargetFramework}\\{referenced.ProjectName}.*";

                files.Add(new XElement(ns + "file",
                    new XAttribute("src", relativeOutputPath),
                    new XAttribute("target", $"lib\\{packageTfm}")));
            }

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(ns + "package", metadata, files));

            Directory.CreateDirectory(Path.GetDirectoryName(nuspecPath)!);

            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                OmitXmlDeclaration = false,
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace
            };

            using var writer = XmlWriter.Create(nuspecPath, settings);
            document.Save(writer);

            return NuspecWriteResult.Ok();
        }
        catch (Exception ex)
        {
            return NuspecWriteResult.Fail($"Failed writing nuspec '{nuspecPath}': {ex.Message}");
        }
    }

    private static PropertyMapResult BuildPropertyMap(string projectPath)
    {
        try
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);

            var directories = new List<DirectoryInfo>();
            for (var current = directory; current is not null; current = current.Parent)
            {
                directories.Add(current);
            }

            directories.Reverse();
            foreach (var dir in directories)
            {
                var propsPath = Path.Combine(dir.FullName, "Directory.Build.props");
                if (!File.Exists(propsPath))
                {
                    continue;
                }

                var props = CsprojParser.ReadPropsProperties(propsPath);
                foreach (var kv in props)
                {
                    map[kv.Key] = kv.Value;
                }
            }

            var projectProps = CsprojParser.ReadProjectProperties(projectPath);
            foreach (var kv in projectProps)
            {
                map[kv.Key] = kv.Value;
            }

            return PropertyMapResult.Ok(map);
        }
        catch (Exception ex)
        {
            return PropertyMapResult.Fail($"Failed loading properties for {projectPath}: {ex.Message}");
        }
    }

    private static string ResolveProperties(string value, IReadOnlyDictionary<string, string> properties, ISet<string> unresolved)
    {
        return PropertyPattern.Replace(value, match =>
        {
            var propertyName = match.Groups[1].Value;
            if (properties.TryGetValue(propertyName, out var propertyValue) && !string.IsNullOrWhiteSpace(propertyValue))
            {
                return propertyValue;
            }

            unresolved.Add(propertyName);
            return match.Value;
        });
    }
}
