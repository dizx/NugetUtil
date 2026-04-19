using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

internal static class FoDeployablePackageService
{
    private static readonly Regex VersionPattern = new(@"\d+\.\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly string[] IncludedRootFolders = ["bin", "AdditionalFiles", "Reports", "Resources"];
    private const string EmptyFolderPlaceholderFileName = "_nugetutil.keep";

    public static async Task<FoDeployablePackResult> BuildAsync(
        string packageSourcePath,
        string outputFolder,
        string workingDirectory,
        bool saveNuspecToOutput,
        bool whatIf)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "NugetUtil", "fopack", Guid.NewGuid().ToString("N"));

        try
        {
            var extractResult = ExtractPayload(packageSourcePath, tempRoot);
            if (!extractResult.Success)
            {
                return FoDeployablePackResult.Fail(extractResult.Error!);
            }

            var payload = extractResult.Payload!;

            var nuspecPath = Path.Combine(tempRoot, payload.PackageId + ".nuspec");
            var nuspecWriteResult = WriteNuspec(nuspecPath, payload.PackageId, payload.Version, payload.XrefFileName, payload.PresentRoots);
            if (!nuspecWriteResult.Success)
            {
                return FoDeployablePackResult.Fail(nuspecWriteResult.Error!);
            }

            string? exportedNuspecPath = null;
            if (saveNuspecToOutput && !whatIf)
            {
                exportedNuspecPath = Path.Combine(outputFolder, payload.PackageId + ".nuspec");
                File.Copy(nuspecPath, exportedNuspecPath, overwrite: true);
            }

            var packResult = await ProcessRunner.RunAsync(
                fileName: "dotnet",
                arguments:
                [
                    "pack",
                    nuspecPath,
                    "-o",
                    outputFolder
                ],
                workingDirectory: workingDirectory,
                whatIf: whatIf,
                sensitiveValues: [],
                printOutputOnSuccess: false);

            if (!packResult.Success)
            {
                return FoDeployablePackResult.Fail(packResult.Error!);
            }

            var nupkgPath = ResolvePackedNupkgPath(outputFolder, payload.PackageId, payload.Version);
            if (!whatIf && string.IsNullOrWhiteSpace(nupkgPath))
            {
                var expectedRaw = Path.Combine(outputFolder, $"{payload.PackageId}.{payload.Version}.nupkg");
                var normalizedVersion = NormalizeVersionForNupkgFileName(payload.Version);
                var expectedNormalized = Path.Combine(outputFolder, $"{payload.PackageId}.{normalizedVersion}.nupkg");
                return FoDeployablePackResult.Fail(
                    $"Pack succeeded but expected file not found. Checked: {expectedRaw}; {expectedNormalized}");
            }

            return FoDeployablePackResult.Ok(
                payload.PackageId,
                payload.Version,
                nupkgPath ?? Path.Combine(outputFolder, $"{payload.PackageId}.{payload.Version}.nupkg"),
                exportedNuspecPath);
        }
        catch (Exception ex)
        {
            return FoDeployablePackResult.Fail($"Failed to pack Dynamics 365 FO package source '{packageSourcePath}': {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static FoPayloadExtractResult ExtractPayload(string packageSourcePath, string stagingRoot)
    {
        if (Directory.Exists(packageSourcePath))
        {
            return StagePayloadFromDirectory(packageSourcePath, stagingRoot);
        }

        if (File.Exists(packageSourcePath))
        {
            return StagePayloadFromDeployableZip(packageSourcePath, stagingRoot);
        }

        return FoPayloadExtractResult.Fail($"Dynamics 365 FO package source does not exist: {packageSourcePath}");
    }

    private static FoPayloadExtractResult StagePayloadFromDeployableZip(string deployablePackagePath, string stagingRoot)
    {
        using var outerArchive = ZipFile.OpenRead(deployablePackagePath);
        var innerZipEntry = outerArchive.Entries
            .FirstOrDefault(entry => IsInnerPayloadZip(entry.FullName));

        if (innerZipEntry is null)
        {
            return FoPayloadExtractResult.Fail("Could not find payload zip under AOSService/Packages/files/*.zip.");
        }

        using var innerZipBuffer = new MemoryStream();
        using (var innerZipEntryStream = innerZipEntry.Open())
        {
            innerZipEntryStream.CopyTo(innerZipBuffer);
        }

        innerZipBuffer.Position = 0;
        using var innerArchive = new ZipArchive(innerZipBuffer, ZipArchiveMode.Read, leaveOpen: false);

        var xrefEntry = innerArchive.Entries
            .FirstOrDefault(entry => IsRootXref(entry.FullName));

        if (xrefEntry is null)
        {
            return FoPayloadExtractResult.Fail("Could not find root *.xref file in payload zip.");
        }

        var xrefFileName = Path.GetFileName(NormalizeZipPath(xrefEntry.FullName));
        var packageId = Path.GetFileNameWithoutExtension(xrefFileName);
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return FoPayloadExtractResult.Fail("Could not determine package id from root .xref file.");
        }

        var dllEntryPath = $"bin/Dynamics.AX.{packageId}.dll";
        var dllEntry = innerArchive.Entries
            .FirstOrDefault(entry => string.Equals(NormalizeZipPath(entry.FullName), dllEntryPath, StringComparison.OrdinalIgnoreCase));

        if (dllEntry is null)
        {
            return FoPayloadExtractResult.Fail($"Could not find '{dllEntryPath}' in payload zip.");
        }

        Directory.CreateDirectory(stagingRoot);
        var presentRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in innerArchive.Entries)
        {
            var normalized = NormalizeZipPath(entry.FullName);
            if (IsDirectory(entry.FullName))
            {
                if (IsSelectedPayloadPath(normalized))
                {
                    AddPresentRoot(normalized, presentRoots);
                    EnsureDirectoryEntry(normalized, stagingRoot);
                }

                continue;
            }

            if (string.Equals(normalized, NormalizeZipPath(xrefEntry.FullName), StringComparison.OrdinalIgnoreCase) ||
                IsSelectedPayloadPath(normalized))
            {
                AddPresentRoot(normalized, presentRoots);
                ExtractEntry(entry, stagingRoot);
            }
        }

        EnsureIncludedFoldersMaterialized(stagingRoot, presentRoots);

        var extractedDllPath = Path.Combine(stagingRoot, dllEntryPath.Replace('/', Path.DirectorySeparatorChar));
        var versionResult = ReadNugetVersionFromFileVersion(extractedDllPath);
        if (!versionResult.Success)
        {
            return FoPayloadExtractResult.Fail(versionResult.Error!);
        }

        var payload = new FoPayloadInfo(packageId, versionResult.Version!, xrefFileName, presentRoots);
        return FoPayloadExtractResult.Ok(payload);
    }

    private static FoPayloadExtractResult StagePayloadFromDirectory(string sourceDirectory, string stagingRoot)
    {
        Directory.CreateDirectory(stagingRoot);

        var xrefPath = Directory.EnumerateFiles(sourceDirectory, "*.xref", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(xrefPath))
        {
            return FoPayloadExtractResult.Fail("Could not find root *.xref file in Dynamics 365 FO source directory.");
        }

        var xrefFileName = Path.GetFileName(xrefPath);
        var packageId = Path.GetFileNameWithoutExtension(xrefFileName);
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return FoPayloadExtractResult.Fail("Could not determine package id from root .xref file.");
        }

        var presentRoots = CopyDirectoryPayload(sourceDirectory, stagingRoot, xrefPath);
        EnsureIncludedFoldersMaterialized(stagingRoot, presentRoots);

        var dllPath = Path.Combine(stagingRoot, "bin", $"Dynamics.AX.{packageId}.dll");
        var versionResult = ReadNugetVersionFromFileVersion(dllPath);
        if (!versionResult.Success)
        {
            return FoPayloadExtractResult.Fail(versionResult.Error!);
        }

        var payload = new FoPayloadInfo(packageId, versionResult.Version!, xrefFileName, presentRoots);
        return FoPayloadExtractResult.Ok(payload);
    }

    private static bool IsInnerPayloadZip(string fullName)
    {
        var normalized = NormalizeZipPath(fullName);
        return normalized.StartsWith("AOSService/Packages/files/", StringComparison.OrdinalIgnoreCase) &&
               normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootXref(string fullName)
    {
        if (IsDirectory(fullName))
        {
            return false;
        }

        var normalized = NormalizeZipPath(fullName);
        return !normalized.Contains('/') && normalized.EndsWith(".xref", StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractEntry(ZipArchiveEntry entry, string rootPath)
    {
        var normalized = NormalizeZipPath(entry.FullName);
        var destinationPath = Path.GetFullPath(Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootFullPath = Path.GetFullPath(rootPath) + Path.DirectorySeparatorChar;
        if (!destinationPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Entry path escapes staging root: {entry.FullName}");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using var source = entry.Open();
        using var destination = File.Create(destinationPath);
        source.CopyTo(destination);
    }

    private static void EnsureDirectoryEntry(string normalizedPath, string rootPath)
    {
        var relativePath = normalizedPath.TrimEnd('/');
        var destinationPath = Path.GetFullPath(Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootFullPath = Path.GetFullPath(rootPath) + Path.DirectorySeparatorChar;
        if (!destinationPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Directory path escapes staging root: {normalizedPath}");
        }

        Directory.CreateDirectory(destinationPath);
    }

    private static void EnsureIncludedFoldersMaterialized(string stagingRoot, IReadOnlySet<string> presentRoots)
    {
        foreach (var root in presentRoots)
        {
            var rootPath = Path.Combine(stagingRoot, root);
            Directory.CreateDirectory(rootPath);

            var hasFiles = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).Any();
            if (hasFiles)
            {
                continue;
            }

            var placeholderPath = Path.Combine(rootPath, EmptyFolderPlaceholderFileName);
            if (!File.Exists(placeholderPath))
            {
                File.WriteAllText(placeholderPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
    }

    private static HashSet<string> CopyDirectoryPayload(string sourceDirectory, string stagingRoot, string xrefPath)
    {
        var presentRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        File.Copy(xrefPath, Path.Combine(stagingRoot, Path.GetFileName(xrefPath)), overwrite: true);

        foreach (var root in IncludedRootFolders)
        {
            var sourceRoot = Path.Combine(sourceDirectory, root);
            if (!Directory.Exists(sourceRoot))
            {
                continue;
            }

            presentRoots.Add(root);

            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(stagingRoot, relative));
            }

            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, file);
                var destination = Path.Combine(stagingRoot, relative);
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(file, destination, overwrite: true);
            }
        }

        return presentRoots;
    }

    private static VersionReadResult ReadNugetVersionFromFileVersion(string dllPath)
    {
        if (!File.Exists(dllPath))
        {
            return VersionReadResult.Fail($"Expected DLL not found after extraction: {dllPath}");
        }

        var fileInfo = FileVersionInfo.GetVersionInfo(dllPath);
        var candidate = fileInfo.FileVersion;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = fileInfo.ProductVersion;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return VersionReadResult.Fail($"Could not read file version from '{Path.GetFileName(dllPath)}'.");
        }

        var match = VersionPattern.Match(candidate);
        if (!match.Success)
        {
            return VersionReadResult.Fail($"File version '{candidate}' is not a valid NuGet version.");
        }

        return VersionReadResult.Ok(match.Value);
    }

    private static string NormalizeZipPath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static bool IsDirectory(string path)
        => path.EndsWith("/", StringComparison.Ordinal) || path.EndsWith("\\", StringComparison.Ordinal);

    private static string? ResolvePackedNupkgPath(string outputFolder, string packageId, string version)
    {
        var rawPath = Path.Combine(outputFolder, $"{packageId}.{version}.nupkg");
        if (File.Exists(rawPath))
        {
            return rawPath;
        }

        var normalizedVersion = NormalizeVersionForNupkgFileName(version);
        var normalizedPath = Path.Combine(outputFolder, $"{packageId}.{normalizedVersion}.nupkg");
        if (File.Exists(normalizedPath))
        {
            return normalizedPath;
        }

        return null;
    }

    private static string NormalizeVersionForNupkgFileName(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        var dashIndex = version.IndexOf('-');
        var plusIndex = version.IndexOf('+');
        var suffixIndex = dashIndex >= 0 && plusIndex >= 0
            ? Math.Min(dashIndex, plusIndex)
            : Math.Max(dashIndex, plusIndex);

        var core = suffixIndex >= 0 ? version[..suffixIndex] : version;
        var suffix = suffixIndex >= 0 ? version[suffixIndex..] : string.Empty;

        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 3 && string.Equals(parts[^1], "0", StringComparison.Ordinal))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return string.Join('.', parts) + suffix;
    }

    private static bool IsSelectedPayloadPath(string normalizedPath)
        => IncludedRootFolders.Any(root =>
            normalizedPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));

    private static void AddPresentRoot(string normalizedPath, ISet<string> presentRoots)
    {
        var root = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(root) && IncludedRootFolders.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            presentRoots.Add(root);
        }
    }

    private static NuspecWriteResult WriteNuspec(string nuspecPath, string packageId, string version, string xrefFileName, IReadOnlySet<string> presentRoots)
    {
        try
        {
            var ns = XNamespace.Get("http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd");
            var fileElements = new List<XElement>();

            foreach (var root in IncludedRootFolders.Where(root => presentRoots.Contains(root)))
            {
                fileElements.Add(new XElement(ns + "file",
                    new XAttribute("src", root + "\\**"),
                    new XAttribute("target", root)));
            }

            fileElements.Add(new XElement(ns + "file",
                new XAttribute("src", xrefFileName),
                new XAttribute("target", string.Empty)));

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(ns + "package",
                    new XElement(ns + "metadata",
                        new XElement(ns + "id", packageId),
                        new XElement(ns + "version", version),
                        new XElement(ns + "title", packageId),
                        new XElement(ns + "authors", packageId),
                        new XElement(ns + "owners", packageId),
                        new XElement(ns + "requireLicenseAcceptance", "false"),
                        new XElement(ns + "description", "Compiled artifacts extracted from a Dynamics 365 FO deployable package."),
                        new XElement(ns + "tags", packageId)),
                    new XElement(ns + "files", fileElements)));

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

    private sealed record FoPayloadInfo(string PackageId, string Version, string XrefFileName, IReadOnlySet<string> PresentRoots);
    private sealed record FoPayloadExtractResult(bool Success, FoPayloadInfo? Payload, string? Error)
    {
        public static FoPayloadExtractResult Ok(FoPayloadInfo payload) => new(true, payload, null);
        public static FoPayloadExtractResult Fail(string error) => new(false, null, error);
    }

    private sealed record VersionReadResult(bool Success, string? Version, string? Error)
    {
        public static VersionReadResult Ok(string version) => new(true, version, null);
        public static VersionReadResult Fail(string error) => new(false, null, error);
    }
}

internal sealed record FoDeployablePackResult(bool Success, string? PackageId, string? Version, string? NupkgPath, string? Error)
{
    public string? ExportedNuspecPath { get; init; }

    public static FoDeployablePackResult Ok(string packageId, string version, string nupkgPath, string? exportedNuspecPath)
        => new(true, packageId, version, nupkgPath, null)
        {
            ExportedNuspecPath = exportedNuspecPath
        };

    public static FoDeployablePackResult Fail(string error)
        => new(false, null, null, null, error);
}
