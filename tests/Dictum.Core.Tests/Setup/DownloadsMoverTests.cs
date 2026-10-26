using Dictum.Setup;
using Dictum.Testing;

namespace Dictum.Core.Tests.Setup;

public sealed class DownloadsMoverTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private AppPaths Current => new AppPaths(_dir.File("config"), _dir.File("data")).WithDownloads(_dir.File("old"));

    private string NewFolder => _dir.File("new");

    private string NewModels => Path.Combine(NewFolder, "models");

    [Fact]
    public void PlanListsEveryFileUnderTheModelsFolder()
    {
        Model("a.bin", 10);
        Model(Path.Combine("sub", "b.onnx"), 20);
        Model("c.bin.partial", 5);

        var move = DownloadsMover.Plan(Current, NewFolder);

        Assert.Equal(Current.ModelsDirectory, move.From);
        Assert.Equal(NewModels, move.To);
        Assert.Equal(["a.bin", "c.bin.partial", Path.Combine("sub", "b.onnx")], move.Files.Order(StringComparer.Ordinal));
        Assert.Equal(35, move.Bytes);
    }

    [Fact]
    public void PlanFindsNothingWhenThereIsNoModelsFolder()
    {
        var move = DownloadsMover.Plan(Current, NewFolder);

        Assert.Empty(move.Files);
        Assert.Equal(0, move.Bytes);
    }

    [Fact]
    public void PlanRefusesTheSameFolderOrOneInsideTheOther()
    {
        Assert.Throws<ArgumentException>(() => DownloadsMover.Plan(Current, _dir.File("old")));
        Assert.Throws<ArgumentException>(() => DownloadsMover.Plan(Current, Path.Combine(Current.ModelsDirectory, "x")));

        var inner = Current.WithDownloads(Path.Combine(NewFolder, "models", "inner"));
        Assert.Throws<ArgumentException>(() => DownloadsMover.Plan(inner, NewFolder));
    }

    [Fact]
    public async Task CopyPutsEveryFileAtTheTargetAndLeavesTheSource()
    {
        var a = Model("a.bin", 3_000_000);
        var b = Model(Path.Combine("sub", "b.onnx"), 20);
        var move = DownloadsMover.Plan(Current, NewFolder);
        var reported = new List<long>();

        await DownloadsMover.CopyAsync(move, long.MaxValue, new SyncProgress<long>(reported.Add), _ct);

        Assert.Equal(a, File.ReadAllBytes(Path.Combine(NewModels, "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(NewModels, "sub", "b.onnx")));
        Assert.Empty(Directory.GetFiles(NewModels, "*.moving", SearchOption.AllDirectories));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(Current.ModelsDirectory, "a.bin")));
        Assert.Equal(move.Bytes, reported[^1]);
        Assert.Equal(reported.Order(), reported);
    }

    [Fact]
    public async Task CopyRefusesTooLittleSpaceBeforeCopying()
    {
        Model("a.bin", 10);
        var move = DownloadsMover.Plan(Current, NewFolder);

        var ex = await Assert.ThrowsAsync<DownloadException>(() => DownloadsMover.CopyAsync(move, 1, null, _ct));

        Assert.StartsWith($"Not enough disk space in {NewModels}:", ex.Message);
        Assert.Empty(FilesAt(NewFolder));
    }

    [Fact]
    public async Task ACancelledCopyLeavesNothingAtTheTarget()
    {
        var a = Model("a.bin", 3_000_000);
        Model("b.bin", 3_000_000);
        var move = DownloadsMover.Plan(Current, NewFolder);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DownloadsMover.CopyAsync(move, long.MaxValue, new SyncProgress<long>(_ => cts.Cancel()), cts.Token));

        Assert.Empty(FilesAt(NewFolder));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(Current.ModelsDirectory, "a.bin")));
        Assert.Equal(2, FilesAt(Current.ModelsDirectory).Length);
    }

    [Fact]
    public async Task AFailedCopyRemovesTheFilesItAlreadyCopied()
    {
        Model("a.bin", 10);
        Model("b.bin", 10);
        var move = DownloadsMover.Plan(Current, NewFolder);
        File.Delete(Path.Combine(Current.ModelsDirectory, move.Files[1]));

        await Assert.ThrowsAsync<FileNotFoundException>(() => DownloadsMover.CopyAsync(move, long.MaxValue, null, _ct));

        Assert.Empty(FilesAt(NewFolder));
        Assert.Single(FilesAt(Current.ModelsDirectory));
    }

    // Moving back to a folder kept after "Don't move": the copy there is the user's, and a failed move must not delete it.
    [Fact]
    public async Task AFailedCopyKeepsAFileThatWasAlreadyAtTheTarget()
    {
        var a = Model("a.bin", 10);
        Model("b.bin", 10);
        Directory.CreateDirectory(NewModels);
        File.WriteAllBytes(Path.Combine(NewModels, "a.bin"), a);
        var move = DownloadsMover.Plan(Current, NewFolder) with { Files = ["a.bin", "b.bin", "gone.bin"] };

        await Assert.ThrowsAsync<FileNotFoundException>(() => DownloadsMover.CopyAsync(move, long.MaxValue, null, _ct));

        Assert.Equal([Path.Combine(NewModels, "a.bin")], FilesAt(NewFolder));
    }

    // A drive unplugged between the question and Move: a clear error at once, never a search for a parent that is not there.
    [Fact(Timeout = TestTimeout.Default)]
    public async Task ATargetOnADriveThatIsGoneFailsAtOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows has drive roots that can be missing");
        var gone = Path.Combine(Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").First(root => !Directory.Exists(root)), "Dictum");
        Model("a.bin", 10);
        var move = DownloadsMover.Plan(Current, gone);

        var ex = await Assert.ThrowsAsync<DownloadException>(() => DownloadsMover.CopyAsync(move, long.MaxValue, null, TestContext.Current.CancellationToken));

        Assert.Equal(DownloadsFolder.MissingError(gone), ex.Message);
        Assert.Single(FilesAt(Current.ModelsDirectory));
    }

    [Fact]
    public async Task DeleteSourcesRemovesTheOldFilesAndEmptyFolders()
    {
        Model("a.bin", 10);
        Model(Path.Combine("sub", "b.onnx"), 20);
        var move = DownloadsMover.Plan(Current, NewFolder);
        await DownloadsMover.CopyAsync(move, long.MaxValue, null, _ct);

        var left = DownloadsMover.DeleteSources(move);

        Assert.Empty(left);
        Assert.False(Directory.Exists(Current.ModelsDirectory));
        Assert.True(Directory.Exists(_dir.File("old")));
        Assert.Equal(2, FilesAt(NewModels).Length);
    }

    private byte[] Model(string relative, int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        var path = Path.Combine(Current.ModelsDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return data;
    }

    private static string[] FilesAt(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];
}
