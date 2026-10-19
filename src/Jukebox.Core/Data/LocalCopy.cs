namespace Jukebox.Core.Data;

public static class LocalCopy
{
    public const string SourceId = "local-media";
    public const string OnlimeSourceId = "onlime-media";

    public static string? DeletablePath(string sourceId, string libraryRoot, string onlimeRoot, string relativePath)
    {
        var root = sourceId switch
        {
            SourceId => libraryRoot,
            OnlimeSourceId => onlimeRoot,
            _ => null
        };
        return root is null ? null : InsideLibrary(root, relativePath);
    }

    public static string? InsideLibrary(string libraryRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(relativePath))
            return null;
        var root = Path.GetFullPath(libraryRoot);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        return full;
    }
}
