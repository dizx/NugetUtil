internal sealed record ProjectInfo(
    string Path,
    string ProjectName,
    string PackageId,
    string Version,
    string TargetFramework,
    string Product,
    string Authors,
    string Company,
    string Description,
    string PackageTags,
    bool IsPackable,
    bool IsPackage,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<PackageRef> PackageReferences);

internal sealed record PackageRef(string Id, string Version);

internal sealed record ParseProjectResult(bool Success, ProjectInfo? Project, string? Error)
{
    public static ParseProjectResult Ok(ProjectInfo project) => new(true, project, null);
    public static ParseProjectResult Fail(string error) => new(false, null, error);
}

internal sealed record ProjectDiscoveryResult(bool Success, Dictionary<string, ProjectInfo>? Projects, string? Error)
{
    public static ProjectDiscoveryResult Ok(Dictionary<string, ProjectInfo> projects) => new(true, projects, null);
    public static ProjectDiscoveryResult Fail(string error) => new(false, null, error);
}

internal sealed record DependencyResult(bool Success, IReadOnlyList<PackageRef>? Dependencies, string? Error)
{
    public static DependencyResult Ok(IReadOnlyList<PackageRef> dependencies) => new(true, dependencies, null);
    public static DependencyResult Fail(string error) => new(false, null, error);
}

internal sealed record PropertyMapResult(bool Success, IReadOnlyDictionary<string, string>? Properties, string? Error)
{
    public static PropertyMapResult Ok(IReadOnlyDictionary<string, string> properties) => new(true, properties, null);
    public static PropertyMapResult Fail(string error) => new(false, null, error);
}

internal sealed record NuspecWriteResult(bool Success, string? Error)
{
    public static NuspecWriteResult Ok() => new(true, null);
    public static NuspecWriteResult Fail(string error) => new(false, error);
}

internal sealed record ProcessResult(bool Success, string? Error)
{
    public static ProcessResult Ok() => new(true, null);
    public static ProcessResult Fail(string error) => new(false, error);
}
