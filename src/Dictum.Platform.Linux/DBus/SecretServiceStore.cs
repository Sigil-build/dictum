using System.Text;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// Secrets in the freedesktop Secret Service (KWallet on Plasma 6, GNOME Keyring), stored as items with
/// attributes application=dictum and name=&lt;key&gt; in the default collection. Uses the "plain" session:
/// the secret travels over the local session bus only.
/// </summary>
public sealed class SecretServiceStore(Portal portal, ILog log) : ISecretStore, IDisposable
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string DefaultCollection = "/org/freedesktop/secrets/aliases/default";
    private const string NoPrompt = "/";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";

    // Every Dictum secret is stored under, and found by, these attributes.
    private const string ApplicationAttribute = "application";
    private const string ApplicationValue = "dictum";
    private const string NameAttribute = "name";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _session;

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var (connection, session) = await OpenAsync(ct).ConfigureAwait(false);
        var (unlocked, locked) = await CallAsync(connection, c => BuildSearch(c, key), ReadSearch).ConfigureAwait(false);
        if (unlocked.Length == 0 && locked.Length > 0)
        {
            unlocked = await UnlockAsync(connection, locked, ct).ConfigureAwait(false);
        }

        if (unlocked.Length == 0)
        {
            return null;
        }

        var secrets = await CallAsync(connection, c => BuildGetSecrets(c, unlocked, session), ReadSecrets).ConfigureAwait(false);
        return secrets.Count > 0 ? secrets[0] : null;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var (connection, session) = await OpenAsync(ct).ConfigureAwait(false);
        var (_, prompt) = await CallAsync(
            connection,
            c => BuildCreateItem(c, key, value, session),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Item: reader.ReadObjectPathAsString(), Prompt: reader.ReadObjectPathAsString());
            }).ConfigureAwait(false);
        await PromptAsync(connection, prompt, ct).ConfigureAwait(false);
        log.Debug($"Stored secret '{key}' in the Secret Service");
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var (connection, _) = await OpenAsync(ct).ConfigureAwait(false);
        var (unlocked, locked) = await CallAsync(connection, c => BuildSearch(c, key), ReadSearch).ConfigureAwait(false);
        foreach (var item in unlocked.Concat(locked))
        {
            var prompt = await CallAsync(
                connection,
                c => BuildItemDelete(c, item),
                static (message, _) => message.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
            await PromptAsync(connection, prompt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the gate only; the portal connection is shared and stays with its owner.</summary>
    public void Dispose() => _gate.Dispose();

    private async Task<(DBusConnection Connection, string Session)> OpenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = await portal.ConnectAsync(ct).ConfigureAwait(false);
            _session ??= await CallAsync(
                connection,
                BuildOpenSession,
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    reader.ReadVariantValue();
                    return reader.ReadObjectPathAsString();
                }).ConfigureAwait(false);
            return (connection, _session);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string[]> UnlockAsync(DBusConnection connection, string[] items, CancellationToken ct)
    {
        var (unlocked, prompt) = await CallAsync(
            connection,
            c => BuildUnlock(c, items),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Unlocked: reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray(), Prompt: reader.ReadObjectPathAsString());
            }).ConfigureAwait(false);
        if (prompt == NoPrompt)
        {
            return unlocked;
        }

        await PromptAsync(connection, prompt, ct).ConfigureAwait(false);
        return items;
    }

    /// <summary>Shows the keyring's own dialog (e.g. to unlock the wallet); throws when it is dismissed.</summary>
    private static async Task PromptAsync(DBusConnection connection, string prompt, CancellationToken ct)
    {
        if (prompt == NoPrompt)
        {
            return;
        }

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = PromptInterface, Member = "Completed", Path = prompt },
            static (message, _) => message.GetBodyReader().ReadBool(),
            notification =>
            {
                if (notification.HasValue)
                {
                    completed.TrySetResult(!notification.Value);
                }
            },
            emitOnCapturedContext: false,
            ObserverFlags.None).ConfigureAwait(false);

        await connection.CallMethodAsync(BuildPrompt(connection, prompt)).ConfigureAwait(false);
        if (!await completed.Task.WaitAsync(ct).ConfigureAwait(false))
        {
            throw new ConfigException("The keyring prompt was dismissed.");
        }
    }

    private static async Task<T> CallAsync<T>(DBusConnection connection, Func<DBusConnection, MessageBuffer> build, MessageValueReader<T> read)
    {
        try
        {
            return await connection.CallMethodAsync(build(connection), read).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName == DBusNames.ServiceUnknownError)
        {
            throw new ConfigException("No Secret Service is running (KWallet or GNOME Keyring); use 'env:VAR' for API keys instead.");
        }
        catch (DBusErrorReplyException ex)
        {
            throw new ConfigException($"The Secret Service refused the request: {ex.ErrorMessage}");
        }
    }

    private static (string[] Unlocked, string[] Locked) ReadSearch(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        return (
            reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray(),
            reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray());
    }

    /// <summary>Reads a{o(oayays)}: item path → (session, parameters, value, content type).</summary>
    private static List<string> ReadSecrets(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        var values = new List<string>();
        var end = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(end))
        {
            reader.ReadObjectPathAsString();
            reader.AlignStruct();
            reader.ReadObjectPathAsString();
            reader.ReadArrayOfByte();
            var value = reader.ReadArrayOfByte();
            reader.ReadString();
            values.Add(Encoding.UTF8.GetString(value));
        }

        return values;
    }

    private static void WriteAttributes(ref MessageWriter writer, string key)
    {
        var start = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString(ApplicationAttribute);
        writer.WriteString(ApplicationValue);
        writer.WriteDictionaryEntryStart();
        writer.WriteString(NameAttribute);
        writer.WriteString(key);
        writer.WriteDictionaryEnd(start);
    }

    private static MessageBuffer BuildOpenSession(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "OpenSession", "sv");
        writer.WriteString("plain");
        writer.WriteVariant(VariantValue.String(string.Empty));
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildSearch(DBusConnection connection, string key)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "SearchItems", "a{ss}");
            WriteAttributes(ref writer, key);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static MessageBuffer BuildUnlock(DBusConnection connection, string[] items)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "Unlock", "ao");
        writer.WriteArray(items.Select(i => new ObjectPath(i)).ToArray());
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildGetSecrets(DBusConnection connection, string[] items, string session)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "GetSecrets", "aoo");
        writer.WriteArray(items.Select(i => new ObjectPath(i)).ToArray());
        writer.WriteObjectPath(session);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildCreateItem(DBusConnection connection, string key, string value, string session)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, DefaultCollection, "org.freedesktop.Secret.Collection", "CreateItem", "a{sv}(oayays)b");
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["org.freedesktop.Secret.Item.Label"] = $"Dictum: {key}",
            ["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string> { [ApplicationAttribute] = ApplicationValue, [NameAttribute] = key }.AsVariantValue(),
        });
        writer.WriteStructureStart();
        writer.WriteObjectPath(session);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(Encoding.UTF8.GetBytes(value));
        writer.WriteString("text/plain; charset=utf8");
        writer.WriteBool(true);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildItemDelete(DBusConnection connection, string item)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, item, "org.freedesktop.Secret.Item", "Delete", null);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildPrompt(DBusConnection connection, string prompt)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, prompt, PromptInterface, "Prompt", "s");
        writer.WriteString(string.Empty);
        return writer.CreateMessage();
    }
}
