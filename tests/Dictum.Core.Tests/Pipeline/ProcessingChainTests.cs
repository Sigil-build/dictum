using Dictum.Core.Pipeline;
using Dictum.Core.Processing;

namespace Dictum.Core.Tests.Pipeline;

public sealed class ProcessingChainTests
{
    private static readonly ProcessingContext Context = new("p", "uk", null);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachStepGetsTheTextOfTheStepBeforeWithoutInvisibleCharactersOrOuterSpaces()
    {
        var observer = new Observer();
        ITextProcessor[] steps =
        [
            new FakeProcessor("a", t => $" \u200B{t} one\uFEFF "),
            new FakeProcessor("b", t => $"{t} two "),
        ];

        var result = await ProcessingChain.RunAsync(steps, "zero", Context, observer, _ct);

        Assert.Equal("zero one two", result);
        Assert.Equal(["start process:a", "done process:a: zero one", "start process:b", "done process:b: zero one two"], observer.Events);
    }

    [Fact]
    public async Task AFailingStepStopsTheRunAfterTheStepsBeforeIt()
    {
        var observer = new Observer();
        var later = new FakeProcessor("c", t => t);
        ITextProcessor[] steps = [new FakeProcessor("a", t => t + " one"), new FakeProcessor("b", _ => throw new InvalidOperationException("broken")), later];

        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessingChain.RunAsync(steps, "zero", Context, observer, _ct));

        Assert.Equal(["start process:a", "done process:a: zero one", "start process:b"], observer.Events);
        Assert.Null(later.LastContext);
    }

    private sealed class Observer : IProcessingObserver
    {
        public List<string> Events { get; } = [];

        public void Starting(string stage) => Events.Add($"start {stage}");

        public void Finished(StageTiming timing, string text) => Events.Add($"done {timing.Stage}: {text}");
    }
}
