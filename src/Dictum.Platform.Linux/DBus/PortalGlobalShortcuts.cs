using Dictum.Core.Diagnostics;
using Dictum.Platform.Hotkeys;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// Global hotkeys on Wayland through org.freedesktop.portal.GlobalShortcuts (KDE Plasma 6, GNOME 48+,
/// Hyprland). The desktop asks once per new shortcut id; Activated / Deactivated signals give
/// press and release, so push-to-talk works.
/// </summary>
public sealed class PortalGlobalShortcuts(Portal portal, ILog log) : IHotkeyService
{
    public const string Interface = "org.freedesktop.portal.GlobalShortcuts";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IDisposable> _subscriptions = [];
    private volatile string? _session;

    public event Action<HotkeyEvent>? Triggered;

    public HotkeyCapabilities Capabilities { get; } = new(
        SupportsRelease: true,
        NeedsUserConfirmation: true,
        CanSuppress: true,
        Backend: "XDG GlobalShortcuts portal");

    /// <summary>Shown when there is no portal: binding the control commands in the compositor always works.</summary>
    public static string CompositorHint =>
        "Bind 'dictum start' on press and 'dictum stop' on release in your compositor, e.g. in Sway: "
        + "bindsym --no-repeat Ctrl+Alt+space exec dictum start; bindsym --release Ctrl+Alt+space exec dictum stop "
        + "(or one key running 'dictum toggle').";

    public async Task SetBindingsAsync(IReadOnlyList<HotkeyBinding> bindings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = await portal.ConnectAsync(ct).ConfigureAwait(false);
            await portal.RegisterHostAppAsync(ct).ConfigureAwait(false);
            if (await portal.GetVersionAsync(Interface, ct).ConfigureAwait(false) is null)
            {
                throw new HotkeyUnavailableException("This desktop has no GlobalShortcuts portal.", CompositorHint);
            }

            await SubscribeAsync(connection).ConfigureAwait(false);

            if (_session is { } previous)
            {
                _session = null;
                await portal.CloseSessionAsync(previous).ConfigureAwait(false);
            }

            if (bindings.Count == 0)
            {
                return;
            }

            var session = await CreateSessionAsync(ct).ConfigureAwait(false);
            await BindAsync(session, bindings, ct).ConfigureAwait(false);
            _session = session;
        }
        catch (DBusErrorReplyException ex) when (Portal.IsMissing(ex))
        {
            throw new HotkeyUnavailableException($"The GlobalShortcuts portal is not available ({ex.ErrorName}).", CompositorHint, ex);
        }
        catch (DBusExceptionBase ex)
        {
            throw new HotkeyUnavailableException($"Talking to the desktop portal failed: {ex.Message}", CompositorHint, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        if (_session is { } session)
        {
            _session = null;
            await portal.CloseSessionAsync(session).ConfigureAwait(false);
        }
    }

    private async Task<string> CreateSessionAsync(CancellationToken ct)
    {
        var token = portal.NextToken();
        var sessionToken = portal.NextToken();
        var (code, results) = await portal.RequestAsync(token, c => BuildCreateSession(c, token, sessionToken), ct).ConfigureAwait(false);
        if (code != 0 || !results.TryGetValue("session_handle", out var handle))
        {
            throw new HotkeyUnavailableException($"The portal refused to create a shortcuts session (code {code}).");
        }

        // The spec types session_handle as 's'; accept an object path too.
        return handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString();
    }

    private async Task BindAsync(string session, IReadOnlyList<HotkeyBinding> bindings, CancellationToken ct)
    {
        var token = portal.NextToken();
        var (code, results) = await portal.RequestAsync(token, c => BuildBindShortcuts(c, session, bindings, token), ct).ConfigureAwait(false);
        if (code == 1)
        {
            throw new HotkeyUnavailableException(
                "Binding the shortcuts was cancelled in the desktop dialog.",
                "Restart 'dictum daemon' and accept the dialog, or assign the shortcuts in System Settings → Shortcuts.");
        }

        if (code != 0)
        {
            throw new HotkeyUnavailableException($"The portal could not bind the shortcuts (code {code}).");
        }

        foreach (var (id, trigger) in ReadTriggers(results))
        {
            log.Info($"Portal bound '{id}' to {trigger}");
        }
    }

    private async Task SubscribeAsync(DBusConnection connection)
    {
        if (_subscriptions.Count > 0)
        {
            return;
        }

        foreach (var (member, phase) in new[] { ("Activated", HotkeyPhase.Pressed), ("Deactivated", HotkeyPhase.Released) })
        {
            _subscriptions.Add(await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = Interface, Member = member, Path = Portal.ObjectPath },
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return (Session: reader.ReadObjectPathAsString(), Id: reader.ReadString());
                },
                notification =>
                {
                    if (notification.HasValue && notification.Value.Session == _session)
                    {
                        Raise(new HotkeyEvent(notification.Value.Id, phase));
                    }
                },
                emitOnCapturedContext: false,
                ObserverFlags.None).ConfigureAwait(false));
        }
    }

    private void Raise(HotkeyEvent hotkeyEvent)
    {
        if (Triggered is { } handler)
        {
            ThreadPool.QueueUserWorkItem(static state => state.handler(state.hotkeyEvent), (handler, hotkeyEvent), preferLocal: false);
        }
    }

    /// <summary>Best effort: the shortcuts result is a(sa{sv}) with a human-readable trigger_description.</summary>
    private static IEnumerable<(string Id, string Trigger)> ReadTriggers(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("shortcuts", out var shortcuts) || shortcuts.Type != VariantValueType.Array)
        {
            yield break;
        }

        for (var i = 0; i < shortcuts.Count; i++)
        {
            var entry = shortcuts.GetItem(i);
            var id = entry.GetItem(0).GetString();
            var details = entry.GetItem(1).GetDictionary<string, VariantValue>();
            yield return (id, details.TryGetValue("trigger_description", out var trigger) ? trigger.GetString() : "(not set)");
        }
    }

    private static MessageBuffer BuildCreateSession(DBusConnection connection, string token, string sessionToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal.Destination, Portal.ObjectPath, Interface, "CreateSession", "a{sv}");
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token,
            ["session_handle_token"] = sessionToken,
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildBindShortcuts(DBusConnection connection, string session, IReadOnlyList<HotkeyBinding> bindings, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal.Destination, Portal.ObjectPath, Interface, "BindShortcuts", "oa(sa{sv})sa{sv}");
        writer.WriteObjectPath(session);
        var array = writer.WriteArrayStart(DBusType.Struct);
        foreach (var binding in bindings)
        {
            writer.WriteStructureStart();
            writer.WriteString(binding.Id);
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["description"] = binding.Description,
                ["preferred_trigger"] = PortalTrigger.Format(binding.Hotkey),
            });
        }

        writer.WriteArrayEnd(array);
        writer.WriteString(string.Empty);
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        return writer.CreateMessage();
    }
}
