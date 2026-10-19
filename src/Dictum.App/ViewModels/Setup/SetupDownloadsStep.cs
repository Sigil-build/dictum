using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Setup;

/// <summary>A row of the step 6 table.</summary>
public sealed record DownloadRow(string File, string Size, string Licence, string Source);

/// <summary>
/// Step 6: the files the plan still needs, with size, licence and source; what is already in place; a privacy
/// line that is true for the chosen server; and the consent. Next ("Download") needs the consent and enough space for
/// the whole set; with nothing to fetch, Next ("Continue") goes straight to the save.
/// </summary>
public sealed partial class SetupDownloadsStep(SetupServices services) : SetupStep
{
    public override string Name => Strings.SetupStepDownloads;

    public string FolderText { get; } = string.Format(CultureInfo.CurrentCulture, Strings.SetupDownloadsLead, services.Paths.ModelsDirectory);

    [ObservableProperty]
    public partial IReadOnlyList<DownloadRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial string TotalText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasFiles { get; private set; }

    [ObservableProperty]
    public partial string? PresentNote { get; private set; }

    [ObservableProperty]
    public partial string PrivacyText { get; private set; } = "";

    [ObservableProperty]
    public partial string? SpaceError { get; private set; }

    /// <summary>Why no plan could be made from the answers.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string? ConfigNote { get; private set; }

    /// <summary>"Download these files now": the consent.</summary>
    [ObservableProperty]
    public partial bool Consent { get; set; } = true;

    public IReadOnlyList<CatalogEntry> Files { get; private set; } = [];

    /// <param name="plan">The plan from the answers, or null when it failed with <paramref name="error"/>.</param>
    /// <param name="wanted">The files the setup uses (speech model, VAD), to name those already in place.</param>
    /// <param name="server">The clean-up server, for the privacy line.</param>
    /// <param name="replacesInvalid">config.json exists but could not be read; finishing replaces it.</param>
    public void Load(SetupPlan? plan, string? error, IReadOnlyList<CatalogEntry> wanted, ExistingLlmServer? server, bool replacesInvalid)
    {
        Error = error;
        Files = plan?.Downloads ?? [];
        Rows = [.. Files.Select(e => new DownloadRow(e.File, Units.Size(e.Size), e.Licence.Name, ModelText.Source(e)))];
        HasFiles = Files.Count > 0;
        TotalText = Units.Size(Files.Sum(e => e.Size));
        var present = wanted.Where(e => !Files.Contains(e)).Select(e => Format(Strings.SetupDownloadsPresentItem, ModelText.Name(e), Units.Size(e.Size))).ToList();
        PresentNote = plan is null || present.Count == 0 ? null : Format(Strings.SetupDownloadsPresent, string.Join(", ", present));
        PrivacyText = server is not null && Uri.TryCreate(server.BaseUrl, UriKind.Absolute, out var url) && !url.IsLoopback
            ? Format(Strings.SetupPrivacyServer, url.Host)
            : Strings.SetupPrivacyLocal;
        var needed = SetupRunner.NeededBytes(services.Paths, Files);
        var free = services.FreeDiskBytes();
        SpaceError = needed > 0 && free < needed ? Format(Strings.SetupDownloadsNoSpace, Units.Size(needed), Units.Size(free)) : null;
        ConfigNote = replacesInvalid ? Strings.SetupDownloadsReplacesConfig : null;
        Update();
    }

    partial void OnConsentChanged(bool value) => Update();

    private void Update() => CanContinue = Error is null && (!HasFiles || (Consent && SpaceError is null));

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
