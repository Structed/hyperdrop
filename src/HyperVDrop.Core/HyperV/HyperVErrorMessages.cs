using System.Globalization;
using System.Text.RegularExpressions;

namespace HyperVDrop.Core.HyperV;

/// <summary>
/// Translates Hyper-V WMI return values and guest-copy failures into messages a user can act on.
/// </summary>
/// <remarks>
/// Hyper-V surfaces failures in three different shapes: the <c>uint</c> return value of the WMI
/// method itself, the <c>ErrorCode</c>/<c>ErrorDescription</c> pair on the resulting
/// <c>Msvm_ConcreteJob</c>, and an HRESULT embedded in the description text. All three are handled
/// here so the UI can show one sentence plus a suggested fix.
/// </remarks>
public static partial class HyperVErrorMessages
{
    /// <summary>Standard CIM return value meaning the operation was started asynchronously.</summary>
    public const uint JobStarted = 4096;

    public const uint Success = 0;

    [GeneratedRegex(@"0x[0-9a-fA-F]{8}", RegexOptions.CultureInvariant)]
    private static partial Regex HResultPattern();

    /// <summary>
    /// Describes the <c>uint</c> value returned directly by an <c>Msvm_*</c> method call.
    /// </summary>
    public static string ForMethodReturn(uint returnValue) => returnValue switch
    {
        Success => "The operation completed successfully.",
        1 => "Hyper-V does not support this operation on this virtual machine.",
        2 => "Hyper-V rejected the operation.",
        3 => "Hyper-V timed out while starting the operation.",
        4 => "Hyper-V rejected one of the parameters.",
        5 => "The virtual machine is not in a state that allows this operation.",
        6 => "Hyper-V rejected a parameter type.",
        JobStarted => "The operation was started.",
        32768 => "Hyper-V reported a general failure.",
        32769 => "Access denied. Run the app as an administrator.",
        32770 => "Hyper-V does not support this operation.",
        32771 => "The virtual machine reported an unknown status.",
        32772 => "The operation timed out.",
        32773 => "Hyper-V rejected one of the parameters.",
        32774 => "The virtual machine is in use by another operation.",
        32775 => "The virtual machine is not in a valid state for this operation. Make sure it is running.",
        32776 => "Hyper-V rejected a data type.",
        32777 => "The virtual machine is not available.",
        32778 => "The host is out of memory.",
        _ => $"Hyper-V returned an unrecognised error code ({returnValue}).",
    };

    /// <summary>
    /// Describes a failed <c>Msvm_ConcreteJob</c>, preferring Hyper-V's own description when it
    /// gave us one.
    /// </summary>
    public static string ForJobFailure(ushort errorCode, string? errorDescription)
    {
        if (!string.IsNullOrWhiteSpace(errorDescription))
        {
            var trimmed = errorDescription.Trim();
            var hresult = TryExtractHResult(trimmed);
            var known = hresult is not null ? DescribeHResult(hresult.Value) : null;

            // Hyper-V's description is often generic ("failed to copy file"), so append the
            // decoded HRESULT when it tells us something more specific.
            return known is null ? trimmed : $"{trimmed} {known}";
        }

        return errorCode == 0
            ? "The file copy failed and Hyper-V did not report a reason."
            : ForMethodReturn(errorCode);
    }

    /// <summary>
    /// Suggests a concrete next step for a failure, or <c>null</c> when nothing obvious applies.
    /// </summary>
    public static string? RemedyFor(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
        {
            return null;
        }

        var hresult = TryExtractHResult(errorText);
        if (hresult is not null)
        {
            var remedy = RemedyForHResult(hresult.Value);
            if (remedy is not null)
            {
                return remedy;
            }
        }

        // Fall back to matching on the message text for cases where no HRESULT was included.
        if (errorText.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("file exists", StringComparison.OrdinalIgnoreCase))
        {
            return "Turn on \"Overwrite existing files\" and retry.";
        }

        if (errorText.Contains("cannot find the path", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("path not found", StringComparison.OrdinalIgnoreCase))
        {
            return "Turn on \"Create destination folders\" and retry.";
        }

        if (errorText.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("integration service", StringComparison.OrdinalIgnoreCase))
        {
            return "Check that integration services are installed and running inside the guest.";
        }

        return null;
    }

    /// <summary>Extracts a trailing <c>0x........</c> HRESULT from a Hyper-V message.</summary>
    internal static uint? TryExtractHResult(string text)
    {
        var match = HResultPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        return uint.TryParse(
            match.Value.AsSpan(2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    internal static string? DescribeHResult(uint hresult) => hresult switch
    {
        0x80070002 => "The source file could not be found.",
        0x80070003 => "The destination folder does not exist in the guest.",
        0x80070005 => "The Hyper-V management service was denied access to the source file.",
        0x80070008 => "The guest is out of memory.",
        0x8007000F => "The destination drive does not exist in the guest.",
        0x80070015 => "The guest file copy service is not ready.",
        0x80070020 => "The source file is locked by another program on the host.",
        0x80070032 => "The guest does not support file copy. Integration services may be missing or outdated.",
        0x80070050 => "A file with that name already exists in the guest.",
        0x80070057 => "Hyper-V rejected the destination path.",
        0x80070070 => "The guest is out of disk space.",
        0x8007007B => "The destination path is not a valid Windows path.",
        0x800700B7 => "A file with that name already exists in the guest.",
        0x800704C7 => "The copy was cancelled.",
        0x800705B4 => "The guest did not respond in time.",
        0x80070542 => "The guest file copy service rejected the request.",
        0x80004005 => "Hyper-V reported an unspecified failure.",
        _ => null,
    };

    private static string? RemedyForHResult(uint hresult) => hresult switch
    {
        0x80070050 or 0x800700B7 => "Turn on \"Overwrite existing files\" and retry.",
        0x80070003 or 0x8007000F => "Turn on \"Create destination folders\", or pick a destination that exists in the guest.",
        0x80070005 => "Move the file to a local folder such as your Desktop and retry. Network and mapped drives are not readable by the Hyper-V service.",
        0x80070032 or 0x80070542 => "Update or reinstall integration services inside the guest, or switch to PowerShell Direct.",
        0x800705B4 or 0x80070015 => "Confirm the guest has finished booting and that the \"Hyper-V Guest Service Interface\" service is running inside it.",
        0x80070070 => "Free up disk space inside the guest and retry.",
        0x8007007B or 0x80070057 => "Check the destination path. It must be an absolute Windows path such as C:\\Users\\Public\\Downloads.",
        _ => null,
    };
}
