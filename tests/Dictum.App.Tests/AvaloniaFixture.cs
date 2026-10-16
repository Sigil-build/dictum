using Avalonia;
using Avalonia.Headless;

[assembly: AssemblyFixture(typeof(Dictum.App.Tests.AvaloniaFixture))]

namespace Dictum.App.Tests;

public static class TestAppBuilder
{
    // Real drawing (Skia), so tests can read pixels; the headless session adds HarfBuzz itself.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// One headless Avalonia session for the test assembly; every test gets a fresh Application (PerTest). Only
/// IAsyncDisposable: a synchronous Dispose of the session hangs the test process under xunit v3 4.0.1.
/// </summary>
public sealed class AvaloniaFixture : IAsyncDisposable
{
    public HeadlessUnitTestSession Session { get; } =
        HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder), AvaloniaTestIsolationLevel.PerTest);

    /// <summary>Runs <paramref name="test"/> on the session's UI thread.</summary>
    public Task Ui(Action test) => Session.Dispatch(test, TestContext.Current.CancellationToken);

    /// <summary>Runs an async test on the UI thread; awaits inside it return to that thread.</summary>
    public Task UiAsync(Func<Task> test) =>
        Session.Dispatch(
            async () =>
            {
                await test();
                return true;
            },
            TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
