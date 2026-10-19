using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Configuration;

public sealed class ConfigPathsTests
{
    [Fact]
    public void PathsUseTheConfigFilesJsonNames()
    {
        Assert.Equal("general.historySize", ConfigPaths.General(nameof(GeneralSettings.HistorySize)));
        Assert.Equal("general.uiLanguage", ConfigPaths.General(nameof(GeneralSettings.UiLanguage)));
        Assert.Equal("general.overlay.opacity", ConfigPaths.Field(ConfigPaths.General(nameof(GeneralSettings.Overlay)), nameof(OverlaySettings.Opacity)));
        Assert.Equal("stt.local", ConfigPaths.Stt("local"));
        Assert.Equal("stt.cloud.timeoutMs", ConfigPaths.Stt("cloud", nameof(OpenAiCompatibleSttConfig.TimeoutMs)));
        Assert.Equal("processors.cleanup.maxTokens", ConfigPaths.Processor("cleanup", nameof(LlmProcessorConfig.MaxTokens)));
        Assert.Equal("processors.terms.rules[1]", ConfigPaths.Item(ConfigPaths.Processor("terms", nameof(ReplaceProcessorConfig.Rules)), 1));
        Assert.Equal("profiles[2].hotkey", ConfigPaths.Profile(2, nameof(ProfileConfig.Hotkey)));
        Assert.Equal("profiles[0].vocabulary[3]", ConfigPaths.Item(ConfigPaths.Profile(0, nameof(ProfileConfig.Vocabulary)), 3));
    }
}
