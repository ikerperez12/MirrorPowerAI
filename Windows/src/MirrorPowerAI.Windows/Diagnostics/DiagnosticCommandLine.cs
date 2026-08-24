namespace MirrorPowerAI.Windows.Diagnostics;

/// <summary>
/// Parses the small, mutually exclusive set of local diagnostic command-line switches.
/// </summary>
internal static class DiagnosticCommandLine
{
    private const string VerifyOverlayArgument = "--verify-overlay";
    private const string VerifyWasapiArgument = "--verify-wasapi";
    private const string VerifyShellArgument = "--verify-shell";
    private const string VerifyUiArgument = "--verify-ui";
    private const string VerifyWhisperRuntimeArgument = "--verify-whisper-runtime";
    private const string RequireAudibleSignalArgument = "--require-audible-signal";

    /// <summary>
    /// Parses recognized diagnostic switches without interpreting normal application arguments.
    /// </summary>
    /// <param name="arguments">The process command-line arguments.</param>
    /// <returns>A validated diagnostic invocation or an invalid result for conflicting switches.</returns>
    internal static DiagnosticInvocation Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var overlayCount = 0;
        var wasapiCount = 0;
        var shellCount = 0;
        var uiCount = 0;
        var whisperRuntimeCount = 0;
        var audibleSignalCount = 0;

        foreach (var argument in arguments)
        {
            if (string.Equals(argument, VerifyOverlayArgument, StringComparison.Ordinal))
            {
                overlayCount++;
            }
            else if (string.Equals(argument, VerifyWasapiArgument, StringComparison.Ordinal))
            {
                wasapiCount++;
            }
            else if (string.Equals(argument, VerifyShellArgument, StringComparison.Ordinal))
            {
                shellCount++;
            }
            else if (string.Equals(argument, VerifyUiArgument, StringComparison.Ordinal))
            {
                uiCount++;
            }
            else if (string.Equals(argument, VerifyWhisperRuntimeArgument, StringComparison.Ordinal))
            {
                whisperRuntimeCount++;
            }
            else if (string.Equals(argument, RequireAudibleSignalArgument, StringComparison.Ordinal))
            {
                audibleSignalCount++;
            }
        }

        var diagnosticCount = overlayCount + wasapiCount + shellCount + uiCount + whisperRuntimeCount;
        if (diagnosticCount == 0)
        {
            return audibleSignalCount == 0
                ? DiagnosticInvocation.None
                : DiagnosticInvocation.Invalid;
        }

        if (diagnosticCount != 1 || audibleSignalCount > 1)
        {
            return DiagnosticInvocation.Invalid;
        }

        if (wasapiCount == 1)
        {
            return new DiagnosticInvocation(DiagnosticKind.Wasapi, audibleSignalCount == 1);
        }

        if (audibleSignalCount != 0)
        {
            return DiagnosticInvocation.Invalid;
        }

        var kind = overlayCount == 1
            ? DiagnosticKind.Overlay
            : shellCount == 1
                ? DiagnosticKind.Shell
                : uiCount == 1
                    ? DiagnosticKind.Ui
                    : DiagnosticKind.WhisperRuntime;
        return new DiagnosticInvocation(kind, RequireAudibleSignal: false);
    }
}

/// <summary>
/// Selects a single local diagnostic or the normal application startup path.
/// </summary>
internal enum DiagnosticKind
{
    /// <summary>No diagnostic was requested.</summary>
    None,

    /// <summary>Verifies WPF capture exclusion.</summary>
    Overlay,

    /// <summary>Verifies WASAPI loopback delivery.</summary>
    Wasapi,

    /// <summary>Verifies tray, mutex, and hotkey shell resources.</summary>
    Shell,

    /// <summary>Verifies one real WPF settings-and-overlay lifecycle without starting a user session.</summary>
    Ui,

    /// <summary>Verifies that the pinned native CPU runtime can load in the published x64 process.</summary>
    WhisperRuntime,

    /// <summary>The switches conflict or violate a diagnostic contract.</summary>
    Invalid,
}

/// <summary>
/// Contains a validated local diagnostic request.
/// </summary>
/// <param name="Kind">The selected diagnostic kind.</param>
/// <param name="RequireAudibleSignal">Whether WASAPI must receive a non-silent sample.</param>
internal readonly record struct DiagnosticInvocation(DiagnosticKind Kind, bool RequireAudibleSignal)
{
    /// <summary>Represents normal application startup with no diagnostic requested.</summary>
    internal static DiagnosticInvocation None { get; } = new(DiagnosticKind.None, false);

    /// <summary>Represents an invalid or conflicting diagnostic request.</summary>
    internal static DiagnosticInvocation Invalid { get; } = new(DiagnosticKind.Invalid, false);
}
