using System.Globalization;
using System.Management;

namespace HyperVDrop.Core.HyperV;

/// <summary>
/// Drives the asynchronous WMI method pattern used throughout <c>root\virtualization\v2</c>:
/// a method returns either success, a failure code, or 4096 plus a reference to an
/// <c>Msvm_ConcreteJob</c> that must be polled to completion.
/// </summary>
/// <remarks>
/// Polling that job is the whole reason this app can show a real progress bar. The equivalent
/// <c>Copy-VMFile</c> cmdlet blocks and discards the job's <c>PercentComplete</c>.
/// </remarks>
internal static class WmiJobRunner
{
    private const ushort JobStateCompleted = 7;
    private const ushort JobStateTerminated = 8;
    private const ushort JobStateKilled = 9;
    private const ushort JobStateException = 10;

    /// <summary>CIM <c>RequestStateChange</c> value that asks a job to stop.</summary>
    private const ushort TerminateRequest = 4;

    /// <summary>How long to keep polling after asking a job to terminate before giving up on it.</summary>
    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interprets the out-parameters of a WMI method call and, when the call started a job,
    /// polls that job until it finishes.
    /// </summary>
    /// <param name="percentComplete">Receives the job's <c>PercentComplete</c> as it advances.</param>
    internal static async Task RunAsync(
        ManagementBaseObject outParameters,
        ManagementScope scope,
        IProgress<int>? percentComplete,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var returnValue = ToUInt32(outParameters["ReturnValue"]);

        if (returnValue == HyperVErrorMessages.Success)
        {
            percentComplete?.Report(100);
            return;
        }

        if (returnValue != HyperVErrorMessages.JobStarted)
        {
            throw new HyperVDropException(HyperVErrorMessages.ForMethodReturn(returnValue));
        }

        if (outParameters["Job"] is not string jobPath || string.IsNullOrWhiteSpace(jobPath))
        {
            throw new HyperVDropException(
                "Hyper-V started the operation but did not return a job to track.");
        }

        await PollAsync(jobPath, scope, percentComplete, pollInterval, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task PollAsync(
        string jobPath,
        ManagementScope scope,
        IProgress<int>? percentComplete,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var path = new ManagementPath(jobPath);
        var terminateRequestedAt = (DateTime?)null;

        while (true)
        {
            using var job = new ManagementObject(scope, path, null);

            try
            {
                job.Get();
            }
            catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.NotFound)
            {
                // Hyper-V reaps completed jobs, so a job that has vanished has finished its work.
                // A failing job stays around long enough for the state check below to see it.
                if (terminateRequestedAt is not null)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                percentComplete?.Report(100);
                return;
            }

            var state = ToUInt16(job["JobState"]);
            percentComplete?.Report(Math.Clamp((int)ToUInt16(job["PercentComplete"]), 0, 100));

            switch (state)
            {
                case JobStateCompleted:
                    percentComplete?.Report(100);
                    return;

                case JobStateTerminated:
                case JobStateKilled:
                    throw new OperationCanceledException(cancellationToken);

                case JobStateException:
                    throw BuildFailure(job);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (terminateRequestedAt is null)
                {
                    terminateRequestedAt = DateTime.UtcNow;
                    TryTerminate(job);
                }
                else if (DateTime.UtcNow - terminateRequestedAt.Value > TerminateGrace)
                {
                    // The job ignored the terminate request; stop waiting on it.
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            // Deliberately not cancellable: we want the loop to observe the terminated job state
            // rather than abandoning a copy that is still running inside Hyper-V.
            await Task.Delay(pollInterval, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static HyperVDropException BuildFailure(ManagementObject job)
    {
        var errorCode = ToUInt16(job["ErrorCode"]);
        var description = job["ErrorDescription"] as string;

        if (string.IsNullOrWhiteSpace(description))
        {
            description = job["ErrorSummaryDescription"] as string;
        }

        var message = HyperVErrorMessages.ForJobFailure(errorCode, description);
        return new HyperVDropException(message, HyperVErrorMessages.RemedyFor(description ?? message));
    }

    private static void TryTerminate(ManagementObject job)
    {
        try
        {
            using var parameters = job.GetMethodParameters("RequestStateChange");
            parameters["RequestedState"] = TerminateRequest;
            using var _ = job.InvokeMethod("RequestStateChange", parameters, null);
        }
        catch (ManagementException)
        {
            // The job may have completed in the gap between polling and cancelling. Nothing to do.
        }
    }

    private static uint ToUInt32(object? value) =>
        value is null ? 0u : Convert.ToUInt32(value, CultureInfo.InvariantCulture);

    private static ushort ToUInt16(object? value) =>
        value is null ? (ushort)0 : Convert.ToUInt16(value, CultureInfo.InvariantCulture);
}
