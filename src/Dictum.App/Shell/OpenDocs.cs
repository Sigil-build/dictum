using Dictum.Core.Docs;

namespace Dictum.App.Shell;

/// <summary>
/// Opens <paramref name="page"/> of Dictum's documentation in the browser, at the section <paramref name="anchor"/>
/// (one of <see cref="DocsAnchors"/>) when it is set, in the UI's language. The shell's opener never throws.
/// </summary>
public delegate void OpenDocs(DocsPage page, string? anchor = null);
