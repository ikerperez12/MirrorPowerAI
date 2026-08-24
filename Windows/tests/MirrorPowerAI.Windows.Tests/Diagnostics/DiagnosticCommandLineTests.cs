using MirrorPowerAI.Windows.Diagnostics;

namespace MirrorPowerAI.Windows.Tests.Diagnostics;

public sealed class DiagnosticCommandLineTests
{
    [Fact]
    public void Parse_NoDiagnosticArguments_SelectsNormalStartup()
    {
        // Act
        var invocation = DiagnosticCommandLine.Parse(["--normal-option"]);

        // Assert
        Assert.Equal(DiagnosticKind.None, invocation.Kind);
        Assert.False(invocation.RequireAudibleSignal);
    }

    [Fact]
    public void Parse_ShellDiagnostic_SelectsOnlyTheShellPath()
    {
        // Act
        var invocation = DiagnosticCommandLine.Parse(["--verify-shell"]);

        // Assert
        Assert.Equal(DiagnosticKind.Shell, invocation.Kind);
        Assert.False(invocation.RequireAudibleSignal);
    }

    [Fact]
    public void Parse_UiDiagnostic_SelectsOnlyTheUiPath()
    {
        // Act
        var invocation = DiagnosticCommandLine.Parse(["--verify-ui"]);

        // Assert
        Assert.Equal(DiagnosticKind.Ui, invocation.Kind);
        Assert.False(invocation.RequireAudibleSignal);
    }

    [Fact]
    public void Parse_WhisperRuntimeDiagnostic_SelectsOnlyTheNativeRuntimePath()
    {
        // Act
        var invocation = DiagnosticCommandLine.Parse(["--verify-whisper-runtime"]);

        // Assert
        Assert.Equal(DiagnosticKind.WhisperRuntime, invocation.Kind);
        Assert.False(invocation.RequireAudibleSignal);
    }

    [Fact]
    public void Parse_WasapiAudibleRequirement_SelectsWasapiWithRequirement()
    {
        // Act
        var invocation = DiagnosticCommandLine.Parse(["--verify-wasapi", "--require-audible-signal"]);

        // Assert
        Assert.Equal(DiagnosticKind.Wasapi, invocation.Kind);
        Assert.True(invocation.RequireAudibleSignal);
    }

    [Theory]
    [MemberData(nameof(InvalidArgumentSets))]
    public void Parse_ConflictingOrDuplicatedDiagnosticArguments_FailsClosed(string[] arguments)
    {
        // Act
        var invocation = DiagnosticCommandLine.Parse(arguments);

        // Assert
        Assert.Equal(DiagnosticKind.Invalid, invocation.Kind);
        Assert.False(invocation.RequireAudibleSignal);
    }

    public static TheoryData<string[]> InvalidArgumentSets =>
    [
        ["--require-audible-signal"],
        ["--verify-shell", "--verify-overlay"],
        ["--verify-shell", "--verify-wasapi"],
        ["--verify-shell", "--verify-ui"],
        ["--verify-shell", "--verify-whisper-runtime"],
        ["--verify-overlay", "--verify-ui"],
        ["--verify-overlay", "--verify-whisper-runtime"],
        ["--verify-wasapi", "--verify-ui"],
        ["--verify-wasapi", "--verify-whisper-runtime"],
        ["--verify-ui", "--verify-whisper-runtime"],
        ["--verify-shell", "--require-audible-signal"],
        ["--verify-overlay", "--require-audible-signal"],
        ["--verify-ui", "--require-audible-signal"],
        ["--verify-whisper-runtime", "--require-audible-signal"],
        ["--verify-wasapi", "--verify-wasapi"],
        ["--verify-shell", "--verify-shell"],
        ["--verify-ui", "--verify-ui"],
        ["--verify-whisper-runtime", "--verify-whisper-runtime"],
        ["--verify-wasapi", "--require-audible-signal", "--require-audible-signal"],
    ];
}
