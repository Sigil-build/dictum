using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Setup;

namespace Dictum.App.Tests.Views;

public sealed class ModelsViewTests(AvaloniaFixture fx) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeDownloads _downloads = new();

    private AppPaths Paths => new(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Install(string file, byte[] data)
    {
        Directory.CreateDirectory(Paths.ModelsDirectory);
        File.WriteAllBytes(Path.Combine(Paths.ModelsDirectory, file), data);
    }

    private (Window Window, ModelsPage Page, SettingsSession Session) Show()
    {
        Directory.CreateDirectory(Paths.ConfigDirectory);
        var store = new ConfigStore(Path.Combine(Paths.ConfigDirectory, "config.json"), NullLog.Instance);
        store.Save(ProfilesPageTests.Sample());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var page = new ModelsPage(session, new ModelsServices(Paths, ModelsPageTests.TestCatalog(), _downloads,
            () => Task.FromResult(ModelsPageTests.IrisXe), () => 48_000_000_000, ModelsPageTests.Memory, 8, new SpeedMeasurements(),
            new FakeChannel(), new ManualTimers(), _ => Task.CompletedTask, TimeProvider.System, NullLog.Instance));
        page.OnShown();
        var window = new Window { Content = new ViewLocator().Build(page), Width = 1100, Height = 1400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, page, session);
    }

    private static List<Border> Cards(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("modelCard"))];

    private static List<Button> Buttons(Window window, int card) =>
        [.. Cards(window)[card].GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)];

    [Fact]
    public Task EachCardShowsTheButtonsForItsState() => fx.Ui(() =>
    {
        Install(ModelsPageTests.TurboFile, ModelsPageTests.TurboData);
        var (window, page, _) = Show();

        _downloads.Set(new QueuedDownload(page.Cards[1].Entry, DownloadState.Downloading, 1_000_000));
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<ModelsView>(window.Content);
        Assert.Equal(3, Cards(window).Count);
        Assert.Equal(["Verify", "Measure speed", "Delete · frees 3 MB"], Buttons(window, 0).Select(b => b.Content as string));
        Assert.False(Buttons(window, 0)[2].IsEffectivelyEnabled);
        Assert.Equal(["Pause", "Cancel"], Buttons(window, 1).Select(b => b.Content as string));
        Assert.Equal(["Download · 1 MB"], Buttons(window, 2).Select(b => b.Content as string));
        Assert.Contains("primary", Buttons(window, 2)[0].Classes);
        window.Close();
    });

    // Review focus 4, in the real dropdown: the pick applies and the dropdown clears itself (4b C1).
    [Fact]
    public Task PickingAProfileUsesTheModelAndClearsThePicker() => fx.Ui(() =>
    {
        Install(ModelsPageTests.SmallFile, ModelsPageTests.SmallData);
        var (window, _, session) = Show();
        var picker = Cards(window)[2].GetVisualDescendants().OfType<ComboBox>().Single(c => c.Classes.Contains("picker"));
        Assert.True(picker.IsEffectivelyVisible);

        picker.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ModelsPageTests.SmallFile, ((WhisperLocalSttConfig)session.Config.Stt["local"]).Model);
        Assert.Null(picker.SelectedItem);
        window.Close();
    });
}
