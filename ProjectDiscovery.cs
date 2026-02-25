internal static class ProjectDiscovery
{
    public static ProjectDiscoveryResult Discover(CliOptions options, NugetUtilConfig config)
    {
        var includeGlobs = options.IncludeGlobs.Select(GlobMatcher.NormalizeGlob).ToList();
        var excludeGlobs = new List<string>();

        if (config.Behavior.ExcludeGlobs is { Count: > 0 })
        {
            excludeGlobs.AddRange(config.Behavior.ExcludeGlobs.Select(GlobMatcher.NormalizeGlob));
        }

        if (options.ExcludeGlobs.Count > 0)
        {
            excludeGlobs.AddRange(options.ExcludeGlobs.Select(GlobMatcher.NormalizeGlob));
        }

        var csprojs = Directory.EnumerateFiles(options.RootPath, "*.csproj", SearchOption.AllDirectories)
            .Where(path => Matches(path, options.RootPath, includeGlobs, excludeGlobs))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var map = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in csprojs)
        {
            var parseResult = CsprojParser.Parse(csproj);
            if (!parseResult.Success)
            {
                return ProjectDiscoveryResult.Fail(parseResult.Error!);
            }

            map[csproj] = parseResult.Project!;
        }

        return ProjectDiscoveryResult.Ok(map);
    }

    private static bool Matches(string fullPath, string rootPath, IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        var normalized = relative.Replace('\\', '/');

        if (excludes.Any(pattern => GlobMatcher.IsMatch(normalized, pattern)))
        {
            return false;
        }

        if (includes.Count == 0)
        {
            return true;
        }

        return includes.Any(pattern => GlobMatcher.IsMatch(normalized, pattern));
    }
}
