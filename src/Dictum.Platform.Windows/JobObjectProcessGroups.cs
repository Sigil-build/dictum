using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dictum.Core;

namespace Dictum.Platform.Windows;

/// <summary>
/// A job object for each group, made to kill every process in it when its handle closes. Only Dictum holds that handle,
/// so the group ends when it is disposed, or when Windows closes Dictum's handles as Dictum's process ends.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class JobObjectProcessGroups : IProcessGroups
{
    public IDisposable Enclose(Process process)
    {
        var job = Win32.CreateJobObject(0, 0);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            var limits = new Win32.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new Win32.JobObjectBasicLimitInformation { LimitFlags = Win32.JobObjectLimitKillOnJobClose },
            };
            if (!Win32.SetInformationJobObject(job, Win32.JobObjectExtendedLimitInformationClass, &limits, (uint)sizeof(Win32.JobObjectExtendedLimitInformation))
                || !Win32.AssignProcessToJobObject(job, process.SafeHandle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }
}
