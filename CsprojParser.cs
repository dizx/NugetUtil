using System.Xml.Linq;

internal static class CsprojParser
{
    public static ParseProjectResult Parse(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
            var properties = ReadProperties(doc.Root);

            var packageId = GetProperty(properties, "PackageId");
            var version = GetProperty(properties, "Version");
            var isPackable = GetProperty(properties, "IsPackable");
            var targetFramework = GetProperty(properties, "TargetFramework");
            var targetFrameworks = GetProperty(properties, "TargetFrameworks");

            if (!string.IsNullOrWhiteSpace(targetFrameworks))
            {
                return ParseProjectResult.Fail($"Multi-target not supported in simplified nuspec mode: {csprojPath}");
            }

            if (string.IsNullOrWhiteSpace(targetFramework))
            {
                return ParseProjectResult.Fail($"Missing TargetFramework in project: {csprojPath}");
            }

            var projectRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csprojPath)!, v!)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var packageRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e =>
                {
                    var include = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value;
                    var versionValue = e.Attribute("Version")?.Value;
                    if (string.IsNullOrWhiteSpace(versionValue))
                    {
                        versionValue = e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value;
                    }

                    return new PackageRef(include ?? string.Empty, versionValue ?? string.Empty);
                })
                .Where(pr => !string.IsNullOrWhiteSpace(pr.Id))
                .ToList();

            var info = new ProjectInfo(
                Path: csprojPath,
                ProjectName: System.IO.Path.GetFileNameWithoutExtension(csprojPath),
                PackageId: packageId,
                Version: version,
                TargetFramework: targetFramework,
                Product: GetProperty(properties, "Product"),
                Authors: GetProperty(properties, "Authors"),
                Company: GetProperty(properties, "Company"),
                Description: GetProperty(properties, "Description"),
                PackageTags: GetProperty(properties, "PackageTags"),
                IsPackable: !string.Equals(isPackable, "false", StringComparison.OrdinalIgnoreCase),
                IsPackage: !string.IsNullOrWhiteSpace(packageId) &&
                           !string.IsNullOrWhiteSpace(version) &&
                           !string.Equals(isPackable, "false", StringComparison.OrdinalIgnoreCase),
                ProjectReferences: projectRefs,
                PackageReferences: packageRefs);

            return ParseProjectResult.Ok(info);
        }
        catch (Exception ex)
        {
            return ParseProjectResult.Fail($"Failed to parse '{csprojPath}': {ex.Message}");
        }
    }

    public static Dictionary<string, string> ReadProjectProperties(string projectPath)
    {
        var doc = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        return ReadProperties(doc.Root);
    }

    public static Dictionary<string, string> ReadPropsProperties(string propsPath)
    {
        var doc = XDocument.Load(propsPath, LoadOptions.PreserveWhitespace);
        return ReadProperties(doc.Root);
    }

    private static Dictionary<string, string> ReadProperties(XElement? root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root is null)
        {
            return map;
        }

        foreach (var prop in root.Descendants().Where(e => e.Parent is not null && e.Parent.Name.LocalName == "PropertyGroup"))
        {
            var key = prop.Name.LocalName;
            var value = prop.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            map[key] = value;
        }

        return map;
    }

    private static string GetProperty(Dictionary<string, string> properties, string key)
        => properties.TryGetValue(key, out var value) ? value : string.Empty;
}
