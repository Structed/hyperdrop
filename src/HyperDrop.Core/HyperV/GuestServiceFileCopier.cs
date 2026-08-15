using System.Management;
using HyperDrop.Core.Abstractions;
using HyperDrop.Core.Model;

namespace HyperDrop.Core.HyperV;

/// <summary>
/// Copies files into a running guest through the Hyper-V Guest Service Interface.
/// </summary>
/// <remarks>
/// <para>
/// This is the preferred engine: it travels over VMBus, so it needs neither guest credentials nor
/// networking in the VM. It calls <c>Msvm_GuestFileService.CopyFilesToGuest</c> and polls the
/// returned <c>Msvm_ConcreteJob</c> for <c>PercentComplete</c>, which is where the progress bar
/// gets its numbers.
/// </para>
/// <para>
/// Requirements: the VM must be running, the "Guest Service Interface" integration service must be
/// enabled, and integration services must be running in the guest (Windows, or Linux with
/// <c>hv_fcopy_daemon</c>).
/// </para>
/// </remarks>
public sealed class GuestServiceFileCopier : IGuestFileCopier
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long the job may sit at 0% before the UI switches to an indeterminate bar. Hyper-V
    /// reports nothing at all while the guest side is opening the file.
    /// </summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(4);

    public string DisplayName => "Guest Service Interface";

    public async Task CopyAsync(
        GuestCopyRequest request,
        IProgress<CopyProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        Validate(request);
        progress.Report(CopyProgress.FromBytes(0, request.SizeBytes));

        // The WMI setup and InvokeMethod calls block, so keep them off the caller's thread.
        var (scope, outParameters) = await Task
            .Run(() => StartCopy(request), cancellationToken)
            .ConfigureAwait(false);

        using (outParameters)
        {
            var tracker = new StallAwareProgress(progress, request.SizeBytes, StallThreshold);

            await WmiJobRunner
                .RunAsync(outParameters, scope, tracker, PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        progress.Report(CopyProgress.Complete(request.SizeBytes));
    }

    /// <summary>Nothing to release: each copy uses a short-lived WMI scope.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static (ManagementScope Scope, ManagementBaseObject OutParameters) StartCopy(GuestCopyRequest request)
    {
        var scope = Wmi.ConnectScope();

        // Msvm_GuestFileService keys on SystemName (the VM GUID), so this is a direct lookup.
        using var fileService = Wmi.QueryFirst(
            scope,
            $"SELECT * FROM Msvm_GuestFileService WHERE SystemName = '{Wmi.EscapeWql(request.VmId)}'")
            ?? throw new HyperDropException(
                $"The guest file service is not available for \"{request.VmName}\".",
                "Make sure the VM is running and that the Guest Service Interface integration service is enabled.");

        using var settings = CreateCopySettings(scope, request);
        using var parameters = fileService.GetMethodParameters("CopyFilesToGuest");

        // The parameter is an array of embedded Msvm_CopyFileToGuestSettingData instances, passed
        // as WMI object text. One file per call, so each copy gets its own trackable job.
        parameters["CopyFileToGuestSettings"] = new[] { settings.GetText(TextFormat.WmiDtd20) };

        try
        {
            var outParameters = fileService.InvokeMethod("CopyFilesToGuest", parameters, null);
            return (scope, outParameters);
        }
        catch (ManagementException ex)
        {
            throw new HyperDropException(
                $"Hyper-V refused the file copy request: {ex.Message.Trim()}",
                HyperVErrorMessages.RemedyFor(ex.Message),
                ex);
        }
    }

    private static ManagementObject CreateCopySettings(ManagementScope scope, GuestCopyRequest request)
    {
        using var settingsClass = new ManagementClass(
            scope,
            new ManagementPath("Msvm_CopyFileToGuestSettingData"),
            null);

        var settings = settingsClass.CreateInstance()
            ?? throw new HyperDropException("Hyper-V would not create the file copy request.");

        settings["SourcePath"] = request.SourcePath;
        settings["DestinationPath"] = request.DestinationPath;
        settings["OverwriteExisting"] = request.OverwriteExisting;
        settings["CreateFullPath"] = request.CreateFullPath;

        return settings;
    }

    private static void Validate(GuestCopyRequest request)
    {
        if (!File.Exists(request.SourcePath))
        {
            throw new HyperDropException($"\"{request.SourcePath}\" no longer exists on this PC.");
        }

        if (!Path.IsPathRooted(request.DestinationPath))
        {
            throw new HyperDropException(
                $"\"{request.DestinationPath}\" is not an absolute path.",
                "The destination must be a full path inside the guest, such as C:\\Users\\Public\\Downloads.");
        }
    }

    /// <summary>
    /// Converts the job's integer percentage into byte-based progress, and falls back to an
    /// indeterminate bar while the job is still reporting nothing.
    /// </summary>
    private sealed class StallAwareProgress(
        IProgress<CopyProgress> inner,
        long totalBytes,
        TimeSpan stallThreshold) : IProgress<int>
    {
        private int _lastPercent = -1;
        private DateTime _lastChangeUtc = DateTime.UtcNow;

        public void Report(int percent)
        {
            if (percent != _lastPercent)
            {
                _lastPercent = percent;
                _lastChangeUtc = DateTime.UtcNow;
            }

            // Only a stall at zero is worth hiding. A bar frozen at 60% is more informative
            // to the user than a marquee.
            if (percent == 0 && DateTime.UtcNow - _lastChangeUtc > stallThreshold)
            {
                inner.Report(CopyProgress.Indeterminate(totalBytes));
                return;
            }

            inner.Report(CopyProgress.FromPercent(percent, totalBytes));
        }
    }
}
