internal sealed class NugetVersionComparer : IComparer<string>
{
    public static readonly NugetVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var xv = Parse(x);
        var yv = Parse(y);

        var maxCoreLength = Math.Max(xv.Core.Count, yv.Core.Count);
        for (var i = 0; i < maxCoreLength; i++)
        {
            var xa = i < xv.Core.Count ? xv.Core[i] : 0;
            var ya = i < yv.Core.Count ? yv.Core[i] : 0;
            var cmp = xa.CompareTo(ya);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        var xHasPre = xv.PreRelease.Count > 0;
        var yHasPre = yv.PreRelease.Count > 0;
        if (xHasPre != yHasPre)
        {
            return xHasPre ? -1 : 1;
        }

        var maxPreLength = Math.Max(xv.PreRelease.Count, yv.PreRelease.Count);
        for (var i = 0; i < maxPreLength; i++)
        {
            if (i >= xv.PreRelease.Count)
            {
                return -1;
            }

            if (i >= yv.PreRelease.Count)
            {
                return 1;
            }

            var cmp = ComparePreSegment(xv.PreRelease[i], yv.PreRelease[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    private static int ComparePreSegment(string x, string y)
    {
        var xNum = int.TryParse(x, out var xn);
        var yNum = int.TryParse(y, out var yn);

        if (xNum && yNum)
        {
            return xn.CompareTo(yn);
        }

        if (xNum != yNum)
        {
            return xNum ? -1 : 1;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    private static ParsedVersion Parse(string version)
    {
        var mainAndPre = version.Split('+', 2)[0].Split('-', 2);
        var core = mainAndPre[0].Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => int.TryParse(segment, out var n) ? n : 0)
            .ToList();

        var pre = mainAndPre.Length > 1
            ? mainAndPre[1].Split('.', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];

        return new ParsedVersion(core, pre);
    }

    private sealed record ParsedVersion(IReadOnlyList<int> Core, IReadOnlyList<string> PreRelease);
}
