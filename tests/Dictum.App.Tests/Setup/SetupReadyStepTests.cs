using Dictum.App.Settings;
using Dictum.App.ViewModels.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupReadyStepTests
{
    // Final review M2: opening the OS page is a Process.Start that can fail; the command never throws.
    [Fact]
    public void OpeningTheTraySettingsNeverThrows()
    {
        var step = new SetupReadyStep(new TrayHint(TrayDesktop.Windows, () => true, () => throw new InvalidOperationException("No shell.")));

        step.OpenTraySettingsCommand.Execute(null);

        Assert.Equal("Ready", step.Name);
    }
}
