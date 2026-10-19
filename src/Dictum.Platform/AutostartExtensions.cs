namespace Dictum.Platform;

public static class AutostartExtensions
{
    /// <summary>
    /// Turns login start on or off and returns whether an entry was written or removed. Turning it on always rewrites
    /// the entry, so it records <paramref name="item"/> even when a copy of Dictum in another folder, or the other kind
    /// of entry, was installed.
    /// </summary>
    public static async Task<bool> ApplyAsync(this IAutostart autostart, bool enable, LoginItem item, CancellationToken ct)
    {
        if (enable)
        {
            await autostart.InstallAsync(item, ct).ConfigureAwait(false);
            return true;
        }

        if (autostart.Installed is null)
        {
            return false;
        }

        await autostart.UninstallAsync(ct).ConfigureAwait(false);
        return true;
    }
}
