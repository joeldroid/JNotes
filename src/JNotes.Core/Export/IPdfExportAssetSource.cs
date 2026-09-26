namespace JNotes.Core.Export;

/// <summary>
/// Static web assets (web-root-relative) the PDF needs. Declared in Core, implemented in JNotes.Web
/// (like <c>ICurrentUserContext</c>) — the files live in Web's wwwroot.
/// </summary>
public interface IPdfExportAssetSource
{
    /// <summary>Opens the asset, or returns <c>null</c> if it's missing. The caller disposes the stream.</summary>
    Stream? Open(string webRootRelativePath);
}

/// <summary>The fixed asset paths PDF export attaches to every request.</summary>
public static class PdfExportAssets
{
    public const string MermaidScript = "lib/mermaid/dist/mermaid.min.js";

    public static IReadOnlyList<string> Fonts { get; } =
    [
        "lib/roboto/roboto-latin-400-normal.woff2",
        "lib/roboto/roboto-latin-500-normal.woff2",
        "lib/roboto/roboto-latin-700-normal.woff2",
    ];

    /// <summary>The flat name of an asset inside the Gotenberg request.</summary>
    public static string FileNameOf(string path) => Path.GetFileName(path);
}
