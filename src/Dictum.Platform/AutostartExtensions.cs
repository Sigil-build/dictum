namespace Dictum.Platform;

public static class AutostartExtensions
{
    /// <summary>
    /// Turns login start on or off. Turning it on always rewrites the entry, so it records <paramref name="executable"/>
    /// even when a copy of Dictum in another folder installed it.
    /// </summary>
    /// <returns>True when the entry was written or removed.</returns>
    public static async Task<bool> ApplyAsync(this IAutostart autostart, bool enable, string executable, CancellationToken ct)
    {
        if (enable)
        {
            await autostart.InstallAsync(executable, ct).ConfigureAwait(false);
            return true;
        }

        if (!autostart.IsInstalled)
        {
            return false;
        }

        await autostart.UninstallAsync(ct).ConfigureAwait(false);
        return true;
    }
}
