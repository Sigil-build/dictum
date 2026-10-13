using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Core.Pipeline;
using Dictum.Core.Processing;
using Dictum.Core.Stt;

namespace Dictum.Core.Tests.Pipeline;

public class DictationPipelineTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly HistoryStore _history;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public DictationPipelineTests() => _history = new HistoryStore(_dir.File("history.jsonl"), 10);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task RecordingFlowsThroughAllStatesAndReturnsProcessedText()
    {
        var audio = new FakeAudioSource(Clips.Seconds(2));
        var pipeline = new DictationPipeline(audio, _history, NullLog.Instance);
        var states = new List<PipelineState>();
        pipeline.StateChanged += states.Add;
        var output = new CollectingOutput();
        var upper = new FakeProcessor("upper", t => t.ToUpperInvariant());
        var profile = Profile(new FakeStt(" привіт світ "), upper);

        await pipeline.StartRecordingAsync(null, _ct);
        var result = await pipeline.StopAsync(profile, output, _ct);

        Assert.Equal(
            [PipelineState.Recording, PipelineState.Transcribing, PipelineState.Processing, PipelineState.Output, PipelineState.Idle],
            states);
        Assert.Equal("привіт світ", result.RawText);
        Assert.Equal("ПРИВІТ СВІТ", result.Text);
        Assert.Equal(["ПРИВІТ СВІТ"], output.Written);
        Assert.Equal(["record", "transcribe", "process:upper", "output"], result.Stages.Select(s => s.Stage));
        Assert.Equal(new ProcessingContext("p", "uk", null), upper.LastContext);
        Assert.Equal("ПРИВІТ СВІТ", Assert.Single(_history.ReadLast(10)).Text);
    }

    [Fact]
    public async Task LineBreaksFromSttNeverReachTheOutput()
    {
        var output = new CollectingOutput();
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), null, NullLog.Instance);

        var result = await pipeline.RunAsync(Clips.Seconds(1), Profile(new FakeStt(" Перевіряю з LamaSwap\n і подивимося \r\n на результат. ")), output, _ct);

        Assert.Equal("Перевіряю з LamaSwap і подивимося на результат.", result.RawText);
        Assert.Equal(["Перевіряю з LamaSwap і подивимося на результат."], output.Written);
    }

    [Theory]
    [InlineData("Дякую за перегляд!", true)]
    [InlineData(" Thanks for watching. ", true)]
    [InlineData("Субтитры сделал DimaTorzok", true)]
    [InlineData("Дякую за перегляд цього документа, колего.", false)]
    [InlineData("Thank you.", false)]
    public void RecognisesSilenceHallucinationsOnlyWhenTheyAreTheWholeText(string text, bool expected) =>
        Assert.Equal(expected, DictationPipeline.IsKnownHallucination(text));

    [Fact]
    public async Task HallucinationIsNotPasted()
    {
        var output = new CollectingOutput();
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), _history, NullLog.Instance);

        var result = await pipeline.RunAsync(Clips.Seconds(1), Profile(new FakeStt("Дякую за перегляд!")), output, _ct);

        Assert.Equal("", result.Text);
        Assert.Empty(output.Written);
        Assert.Empty(_history.ReadLast(10));
    }

    [Fact]
    public async Task ProfileRequestReachesTheProvider()
    {
        var stt = new FakeStt("text");
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), null, NullLog.Instance);

        await pipeline.RunAsync(Clips.Seconds(1), Profile(stt) with { Request = new SttRequest("en", "Avalonia") }, null, _ct);

        Assert.Equal(new SttRequest("en", "Avalonia"), stt.LastRequest);
    }

    [Fact]
    public async Task ProcessorsSeeTheVocabularyAsOneLine()
    {
        var upper = new FakeProcessor("upper", t => t.ToUpperInvariant());
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), null, NullLog.Instance);

        await pipeline.RunAsync(Clips.Seconds(1), Profile(new FakeStt("текст"), upper) with { Vocabulary = ["Notepad++", "VS Code"] }, null, _ct);

        Assert.Equal("Notepad++, VS Code", upper.LastContext!.Vocabulary);
    }

    [Fact]
    public async Task ShortClipIsSkippedWithoutCallingStt()
    {
        var stt = new FakeStt("hallucination");
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(0.2)), _history, NullLog.Instance);

        var result = await pipeline.RunAsync(Clips.Seconds(0.2), Profile(stt), null, _ct);

        Assert.True(result.Skipped);
        Assert.Null(stt.LastRequest);
        Assert.Equal(PipelineState.Idle, pipeline.State);
        Assert.Empty(_history.ReadLast(10));
    }

    [Fact]
    public async Task ProcessorFailureKeepsRawTextInHistoryAndReturnsToIdle()
    {
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), _history, NullLog.Instance);
        var broken = new FakeProcessor("llm", _ => throw new HttpRequestException("down"));

        var ex = await Assert.ThrowsAsync<DictationException>(() =>
            pipeline.RunAsync(Clips.Seconds(1), Profile(new FakeStt("raw words"), broken), null, _ct));

        Assert.Equal("process:llm", ex.Stage);
        Assert.Equal("raw words", ex.RawText);
        Assert.Equal(PipelineState.Idle, pipeline.State);
        var entry = Assert.Single(_history.ReadLast(10));
        Assert.Equal("raw words", entry.Text);
        Assert.Contains("down", entry.Error);
    }

    [Fact]
    public async Task SttFailureLeavesNoHistory()
    {
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), _history, NullLog.Instance);
        var stt = new FakeStt("") { Throw = new InvalidOperationException("model missing") };

        var ex = await Assert.ThrowsAsync<DictationException>(() => pipeline.RunAsync(Clips.Seconds(1), Profile(stt), null, _ct));

        Assert.Equal("transcribe", ex.Stage);
        Assert.Empty(_history.ReadLast(10));
    }

    [Fact]
    public async Task CancelStopsRecordingAndReturnsToIdle()
    {
        var audio = new FakeAudioSource(Clips.Seconds(1));
        var pipeline = new DictationPipeline(audio, _history, NullLog.Instance);

        await pipeline.StartRecordingAsync(null, _ct);
        await pipeline.CancelAsync();

        Assert.False(audio.Started);
        Assert.Equal(PipelineState.Idle, pipeline.State);
    }

    [Fact]
    public async Task CannotStartTwice()
    {
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), null, NullLog.Instance);
        await pipeline.StartRecordingAsync(null, _ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.StartRecordingAsync(null, _ct));
    }

    [Fact]
    public async Task EmptyTranscriptSkipsProcessingAndOutput()
    {
        var processor = new FakeProcessor("p", t => t + "!");
        var output = new CollectingOutput();
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), null, NullLog.Instance);

        var result = await pipeline.RunAsync(Clips.Seconds(1), Profile(new FakeStt("  "), processor), output, _ct);

        Assert.Equal("", result.Text);
        Assert.Null(processor.LastContext);
        Assert.Empty(output.Written);
    }

    private static readonly string[] Words = ["Notepad++", "VS Code", "Dictum", "тогл"];

    [Theory]
    [InlineData("Notepad++, VS Code, Dictum, тогл.")]
    [InlineData("notepad++ vs code dictum")]
    public void HintEchoIsRecognised(string text) =>
        Assert.True(DictationPipeline.IsHintEcho(text, "Notepad++, VS Code, Dictum, тогл.", Words));

    [Theory]
    [InlineData("VS Code, Dictum")]
    [InlineData("Відкрий Notepad++ і VS Code")]
    [InlineData("")]
    public void RealSpeechWithVocabularyTermsIsKept(string text) =>
        Assert.False(DictationPipeline.IsHintEcho(text, "Notepad++, VS Code, Dictum, тогл.", Words));

    [Fact]
    public async Task HintEchoIsNotPasted()
    {
        var output = new CollectingOutput();
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), _history, NullLog.Instance);
        var profile = Profile(new FakeStt("Notepad++, VS Code, Dictum, тогл.")) with
        {
            Request = new SttRequest("uk", "Notepad++, VS Code, Dictum, тогл."),
            Vocabulary = Words,
        };

        var result = await pipeline.RunAsync(Clips.Seconds(1), profile, output, _ct);

        Assert.Equal("", result.Text);
        Assert.Empty(output.Written);
    }

    [Fact]
    public async Task InvisibleCharactersFromProcessorsNeverReachTheOutput()
    {
        var output = new CollectingOutput();
        var bom = new FakeProcessor("llm", t => "﻿" + t + "​");
        var pipeline = new DictationPipeline(new FakeAudioSource(Clips.Seconds(1)), null, NullLog.Instance);

        await pipeline.RunAsync(Clips.Seconds(1), Profile(new FakeStt("Привіт."), bom), output, _ct);

        Assert.Equal(["Привіт."], output.Written);
    }

    private static DictationProfile Profile(ISpeechToText stt, params ITextProcessor[] processors) =>
        new("p", stt, new SttRequest("uk", null), processors);
}
