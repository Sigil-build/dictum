using System.Text.Json.Nodes;
using Dictum.Core.Diagnostics;
using Dictum.Plugins;
using Dictum.Testing;

namespace Dictum.Core.Tests.Plugins;

public sealed class PluginTrustStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ListLog _log = new();
    private readonly FolderLinks _links = new();

    public void Dispose()
    {
        _links.Dispose();
        _temp.Dispose();
    }

    private string TrustFile => _temp.File("plugin-trust.json");

    private PluginTrustStore NewStore() => new(TrustFile, _log);

    /// <summary>A plugin folder with its program, <c>plugin.exe</c>, and a <c>plugin.json</c> that lists <paramref name="program"/>.</summary>
    private (string Folder, string Program) Plugin(string name, params string[] program)
    {
        var folder = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, "plugin.exe");
        File.WriteAllText(executable, "program " + name);
        WriteManifest(folder, program);
        return (folder, executable);
    }

    private static void WriteManifest(string folder, string[] program, string name = "A")
    {
        var manifest = new JsonObject
        {
            ["protocol"] = PluginProtocol.Version,
            ["id"] = "a",
            ["name"] = name,
            ["version"] = "1.0.0",
            ["executable"] = new JsonObject { [PluginProtocol.CurrentRid] = "plugin.exe" },
            ["program"] = new JsonArray([.. program.Select(entry => JsonValue.Create(entry))]),
        };
        File.WriteAllText(Path.Combine(folder, PluginProtocol.ManifestFile), manifest.ToJsonString());
    }

    /// <summary>Writes <paramref name="text"/> to the file at <paramref name="relative"/> in the folder, making its folders.</summary>
    private static void Put(string folder, string relative, string text = "print()")
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static PluginTrustResult Changed(string file) => new(PluginTrust.Changed, file);

    [Fact]
    public void AnUnknownPluginIsNotConfirmed()
    {
        var (folder, program) = Plugin("a");

        Assert.Equal(PluginTrust.NotConfirmed, NewStore().Check("a", folder, program).Trust);
    }

    [Fact]
    public void AConfirmedPluginStaysConfirmedUntilItsProgramChanges()
    {
        var (folder, program) = Plugin("a");
        var store = NewStore();

        store.Confirm("a", folder, program);
        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder, program).Trust);
        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder + Path.DirectorySeparatorChar, program).Trust);

        using (var stream = new FileStream(program, FileMode.Append))
        {
            stream.WriteByte(1);
        }

        Assert.Equal(Changed("plugin.exe"), store.Check("a", folder, program));
    }

    [Fact]
    public void AChangedListedFileIsChanged()
    {
        var (folder, program) = Plugin("a", "plugin.py", "lib");
        Put(folder, "plugin.py");
        Put(folder, "lib/util/text.py");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Put(folder, "lib/util/text.py", "print('changed')");
        Assert.Equal(Changed("lib/util/text.py"), store.Check("a", folder, program));

        // Both differ now; the first in ordinal order is named.
        Put(folder, "plugin.py", "print('changed')");
        Assert.Equal(Changed("lib/util/text.py"), store.Check("a", folder, program));
    }

    [Fact]
    public void AChangedUnlistedFileStaysConfirmed()
    {
        var (folder, program) = Plugin("a", "plugin.py");
        Put(folder, "plugin.py");
        Put(folder, "notes.txt", "before");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Put(folder, "notes.txt", "after");
        Put(folder, "new.txt");

        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder, program).Trust);
    }

    [Fact]
    public void AFileAddedToAListedFolderIsChanged()
    {
        var (folder, program) = Plugin("a", "lib");
        Put(folder, "lib/a.py");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Put(folder, "lib/sub/b.py");

        Assert.Equal(Changed("lib/sub/b.py"), store.Check("a", folder, program));
    }

    [Fact]
    public void ARemovedListedFileIsChanged()
    {
        var (folder, program) = Plugin("a", "plugin.py", "lib");
        Put(folder, "plugin.py");
        Put(folder, "lib/a.py");
        Put(folder, "lib/b.py");
        var store = NewStore();
        store.Confirm("a", folder, program);

        File.Delete(Path.Combine(folder, "lib", "b.py"));
        Assert.Equal(Changed("lib/b.py"), store.Check("a", folder, program));

        Put(folder, "lib/b.py");
        File.Delete(Path.Combine(folder, "plugin.py"));
        Assert.Equal(Changed("plugin.py"), store.Check("a", folder, program));
    }

    /// <summary>Ledger line 49: the program itself, gone, is a change.</summary>
    [Fact]
    public void ADeletedProgramIsChanged()
    {
        var (folder, program) = Plugin("a");
        var store = NewStore();
        store.Confirm("a", folder, program);

        File.Delete(program);

        Assert.Equal(Changed("plugin.exe"), store.Check("a", folder, program));
    }

    /// <summary>Python writes its caches next to the modules it imports, so they change without anyone changing the plugin.</summary>
    [Fact]
    public void APycacheFolderInAListedFolderIsIgnored()
    {
        var (folder, program) = Plugin("a", "lib");
        Put(folder, "lib/a.py");
        Put(folder, "lib/__pycache__/a.cpython-312.pyc", "cache");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Put(folder, "lib/__pycache__/a.cpython-312.pyc", "another cache");
        Put(folder, "lib/deep/__pycache__/b.cpython-312.pyc", "cache");

        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder, program).Trust);
        Assert.DoesNotContain("__pycache__", File.ReadAllText(TrustFile), StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedManifestIsChanged()
    {
        var (folder, program) = Plugin("a");
        var store = NewStore();
        store.Confirm("a", folder, program);

        WriteManifest(folder, [], name: "Another name");

        Assert.Equal(Changed("plugin.json"), store.Check("a", folder, program));
    }

    /// <summary>The record names each file by its path in the folder, with <c>/</c> on every system, and its SHA-256.</summary>
    [Fact]
    public void TheRecordHoldsEveryProgramFileByItsRelativePath()
    {
        var (folder, program) = Plugin("a", "plugin.py", "lib");
        Put(folder, "plugin.py", "abc");
        Put(folder, "lib/sub/x.py");

        NewStore().Confirm("a", folder, program);

        var record = JsonNode.Parse(File.ReadAllText(TrustFile))!["custom"]!["a"]!;
        var files = record["files"]!.AsObject();
        Assert.Equal(["lib/sub/x.py", "plugin.exe", "plugin.json", "plugin.py"], files.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", (string?)files["plugin.py"]);
        Assert.Equal(Path.GetFullPath(folder), (string?)record["folder"]);
        Assert.Equal(Path.GetFullPath(program), (string?)record["executable"]);
    }

    /// <summary>The only shape before program files were recorded, which development builds wrote.</summary>
    [Fact]
    public void ARecordWithoutFilesIsNotConfirmed()
    {
        var (folder, program) = Plugin("a");
        var record = new JsonObject
        {
            ["folder"] = folder,
            ["executable"] = program,
            ["sha256"] = PluginTrustStore.HashFile(program),
        };
        File.WriteAllText(TrustFile, new JsonObject { ["version"] = 1, ["custom"] = new JsonObject { ["a"] = record } }.ToJsonString());
        var store = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program).Trust);
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("unusable records (a)", StringComparison.Ordinal));

        store.Confirm("a", folder, program);

        Assert.Equal(PluginTrust.Confirmed, NewStore().Check("a", folder, program).Trust);
    }

    /// <summary>Ledger line 48: a later Dictum may record more than this one checks, so its file is neither trusted nor replaced.</summary>
    [Fact]
    public void ANewerTrustFileConfirmsNothingAndIsNotOverwritten()
    {
        var (folder, program) = Plugin("a");
        var (otherFolder, otherProgram) = Plugin("b");
        NewStore().Confirm("a", folder, program);
        var newer = JsonNode.Parse(File.ReadAllText(TrustFile))!;
        newer["version"] = 2;
        File.WriteAllText(TrustFile, newer.ToJsonString());
        var before = File.ReadAllBytes(TrustFile);
        var store = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program).Trust);
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("newer Dictum", StringComparison.Ordinal));

        var error = Assert.Throws<PluginTrustException>(() => store.Confirm("b", otherFolder, otherProgram));
        Assert.Contains("was written by a newer Dictum", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
        store.Forget("a");

        Assert.Equal(before, File.ReadAllBytes(TrustFile));
    }

    /// <summary>
    /// <c>plugins add</c> takes the hashes before the plugin first runs and records them after: what is recorded is
    /// what the user was shown, not what the program left behind.
    /// </summary>
    [Fact]
    public void AConfirmationRecordsTheFilesAsTheyWereWhenItWasPrepared()
    {
        var (folder, program) = Plugin("a", "plugin.py");
        Put(folder, "plugin.py", "before");
        var store = NewStore();

        var confirmation = store.Prepare("a", folder, program);
        Put(folder, "plugin.py", "after");
        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program).Trust);
        store.Confirm(confirmation);

        Assert.Equal(Changed("plugin.py"), store.Check("a", folder, program));
    }

    [Fact]
    public void ANewerTrustFileRefusesAConfirmationBeforeItIsPrepared()
    {
        var (folder, program) = Plugin("a");
        File.WriteAllText(TrustFile, """{"version":2,"custom":{}}""");

        var error = Assert.Throws<PluginTrustException>(() => NewStore().Prepare("a", folder, program));

        Assert.Contains("was written by a newer Dictum", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version bump is when the shape is likely to change: a file this Dictum cannot read as its own is still a newer
    /// one, not a damaged one, and is neither trusted nor replaced.
    /// </summary>
    [Fact]
    public void ANewerTrustFileOfAnotherShapeIsNotOverwritten()
    {
        var (folder, program) = Plugin("a");
        var newer = new JsonObject
        {
            ["version"] = 2,
            ["custom"] = new JsonObject
            {
                ["a"] = new JsonObject
                {
                    ["folder"] = folder,
                    ["executable"] = program,
                    ["files"] = new JsonObject { ["plugin.exe"] = new JsonObject { ["sha256"] = PluginTrustStore.HashFile(program), ["size"] = 9 } },
                },
            },
        };
        File.WriteAllText(TrustFile, newer.ToJsonString());
        var before = File.ReadAllBytes(TrustFile);
        var store = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program).Trust);
        var warning = Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Contains("was written by a newer Dictum (file version 2; this one reads 1)", warning.Message, StringComparison.Ordinal);

        Assert.Throws<PluginTrustException>(() => store.Confirm("a", folder, program));
        store.Forget("a");

        Assert.Equal(before, File.ReadAllBytes(TrustFile));
    }

    /// <summary>Ledger line 49: Windows' file system ignores case, so a folder spelt in other letters is the same plugin.</summary>
    [Fact]
    public void APathThatDiffersOnlyInCaseIsTheSamePluginOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows ignores case in paths here");
        var (folder, program) = Plugin("a", "lib");
        Put(folder, "lib/x.py");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder.ToUpperInvariant(), program.ToUpperInvariant()).Trust);
    }

    [Fact]
    public void AListedFileThatCannotBeReadIsChanged()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to open a file that another handle holds without sharing");
        var (folder, program) = Plugin("a", "plugin.py");
        Put(folder, "plugin.py");
        var store = NewStore();
        store.Confirm("a", folder, program);

        using var held = new FileStream(Path.Combine(folder, "plugin.py"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(Changed("plugin.py"), store.Check("a", folder, program));
    }

    /// <summary>A linked folder may lead anywhere, even back up the tree, so it is not entered, nor a listed one.</summary>
    [Fact]
    public void ALinkedFolderIsNotEntered()
    {
        var (folder, program) = Plugin("a", "lib", "linked");
        Put(folder, "lib/a.py");
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        Put(elsewhere, "inside.py");
        _links.Add(Path.Combine(folder, "lib", "up"), folder);
        _links.Add(Path.Combine(folder, "lib", "elsewhere"), elsewhere);
        _links.Add(Path.Combine(folder, "linked"), elsewhere);
        var store = NewStore();
        store.Confirm("a", folder, program);

        Put(elsewhere, "inside.py", "changed");
        Put(elsewhere, "new.py");

        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder, program).Trust);

        // The real folder beside the links is still covered.
        Put(folder, "lib/b.py");
        Assert.Equal(Changed("lib/b.py"), store.Check("a", folder, program));
    }

    /// <summary>
    /// A linked folder is not entered, but it is a program file of its own, by where it points: one added to a listed
    /// folder can shadow a module there, as a package <c>lib/foo</c> shadows <c>lib/foo.py</c> in Python.
    /// </summary>
    /// <summary>
    /// <c>.</c> lists the whole folder: a file planted anywhere in it, such as a <c>json.py</c> that Python would import
    /// in place of its own, or a <c>python.cmd</c> that cmd.exe would run, is a change. Python's caches still are not.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void TheWholeFolderCoversAFilePlantedAnywhereInIt(string entry)
    {
        var (folder, program) = Plugin("a", entry);
        Put(folder, "plugin.py");
        Put(folder, "lib/util.py");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Put(folder, "__pycache__/plugin.cpython-312.pyc", "cache");
        Put(folder, "lib/__pycache__/util.cpython-312.pyc", "cache");
        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder, program).Trust);

        Put(folder, "lib/deep/json.py");
        Assert.Equal(Changed("lib/deep/json.py"), store.Check("a", folder, program));

        File.Delete(Path.Combine(folder, "lib", "deep", "json.py"));
        Put(folder, "python.cmd", "@echo planted");
        Assert.Equal(Changed("python.cmd"), store.Check("a", folder, program));
    }

    [Fact]
    public void AFolderEntryWithATrailingSlashCoversTheFolder()
    {
        var (folder, program) = Plugin("a", "lib/");
        Put(folder, "lib/a.py");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Assert.Contains("lib/a.py", JsonNode.Parse(File.ReadAllText(TrustFile))!["custom"]!["a"]!["files"]!.AsObject().Select(f => f.Key));

        Put(folder, "lib/b.py");
        Assert.Equal(Changed("lib/b.py"), store.Check("a", folder, program));
    }

    [Fact]
    public void AnAddedFolderLinkIsChanged()
    {
        var (folder, program) = Plugin("a", "lib");
        Put(folder, "lib/foo.py");
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        Put(elsewhere, "__init__.py");
        var store = NewStore();
        store.Confirm("a", folder, program);

        _links.Add(Path.Combine(folder, "lib", "foo"), elsewhere);

        Assert.Equal(Changed("lib/foo"), store.Check("a", folder, program));
    }

    [Fact]
    public void ARetargetedFolderLinkIsChanged()
    {
        var (folder, program) = Plugin("a", "lib");
        Put(folder, "lib/a.py");
        var (first, second) = (Path.Combine(_temp.Path, "first"), Path.Combine(_temp.Path, "second"));
        Put(first, "x.py");
        Put(second, "x.py");
        var link = Path.Combine(folder, "lib", "linked");
        _links.Add(link, first);
        var store = NewStore();
        store.Confirm("a", folder, program);
        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder, program).Trust);

        _links.Remove(link);
        _links.Add(link, second);
        Assert.Equal(Changed("lib/linked"), store.Check("a", folder, program));

        _links.Remove(link);
        Assert.Equal(Changed("lib/linked"), store.Check("a", folder, program));
    }

    [Fact]
    public void ALinkedFileIsHashedByItsTarget()
    {
        var (folder, program) = Plugin("a", "lib");
        var target = Path.Combine(_temp.Path, "elsewhere", "target.py");
        Put(Path.GetDirectoryName(target)!, "target.py", "before");
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, "lib", "file.py"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This account cannot make symbolic links: {ex.Message}");
        }

        var store = NewStore();
        store.Confirm("a", folder, program);
        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder, program).Trust);

        File.WriteAllText(target, "after");

        Assert.Equal(Changed("lib/file.py"), store.Check("a", folder, program));
    }

    [Fact]
    public void TheSamePluginFromAnotherFolderIsNotConfirmed()
    {
        var (folder, program) = Plugin("a");
        var (otherFolder, otherProgram) = Plugin("b");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", otherFolder, otherProgram).Trust);
    }

    [Fact]
    public void ForgetRemovesTheRecord()
    {
        var (folder, program) = Plugin("a");
        var store = NewStore();
        store.Confirm("a", folder, program);

        store.Forget("a");

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program).Trust);
    }

    [Fact]
    public void ADamagedFileTrustsNothingAndIsReplacedOnConfirm()
    {
        var (folder, program) = Plugin("a");
        File.WriteAllText(TrustFile, "}{ garbage");
        var store = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program).Trust);
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);

        store.Confirm("a", folder, program);

        Assert.Equal(PluginTrust.Confirmed, NewStore().Check("a", folder, program).Trust);
    }

    [Theory]
    [InlineData("""{"version":1,"custom":{"a":null}}""")]
    [InlineData("""{"version":1,"custom":{"a":{}}}""")]
    [InlineData("""{"version":1,"custom":{"a":{"folder":"bad\u0000path","executable":"x","files":{}}}}""")]
    [InlineData("""{"version":1,"custom":{"a":{"folder":"x","executable":"x","files":{"plugin.exe":null}}}}""")]
    public void ARecordOfTheWrongShapeIsNotConfirmedAndIsReplacedOnConfirm(string json)
    {
        var (folder, program) = Plugin("a");
        File.WriteAllText(TrustFile, json);
        var store = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program).Trust);
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);

        store.Confirm("a", folder, program);

        Assert.Equal(PluginTrust.Confirmed, NewStore().Check("a", folder, program).Trust);
    }

    [Fact]
    public void RecordsSurviveANewStore()
    {
        var (folderA, programA) = Plugin("a");
        var (folderB, programB) = Plugin("b");
        var store = NewStore();
        store.Confirm("a", folderA, programA);
        store.Confirm("b", folderB, programB);
        store.Forget("a");

        var fresh = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, fresh.Check("a", folderA, programA).Trust);
        Assert.Equal(PluginTrust.Confirmed, fresh.Check("b", folderB, programB).Trust);
    }

    [Fact]
    public void TheHashIsLowercaseSha256Hex()
    {
        var file = _temp.File("x.bin");
        File.WriteAllText(file, "abc");

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", PluginTrustStore.HashFile(file));
    }
}
