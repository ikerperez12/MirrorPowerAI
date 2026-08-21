using MirrorPowerAI.Windows.Diagnostics;

namespace MirrorPowerAI.Windows.Tests.Diagnostics;

public sealed class UiDiagnosticContractTests
{
    [Fact]
    public void ValidateCriticalControls_AllVisibleAndNamed_Succeeds()
    {
        // Act
        var result = UiDiagnosticContract.ValidateCriticalControls(
        [
            new UiDiagnosticControlSnapshot(true, true, true),
            new UiDiagnosticControlSnapshot(true, true, true),
        ]);

        // Assert
        Assert.Equal(UiDiagnosticFailure.None, result);
    }

    [Fact]
    public void ValidateCriticalControls_EmptySet_FailsClosed()
    {
        // Act
        var result = UiDiagnosticContract.ValidateCriticalControls([]);

        // Assert
        Assert.Equal(UiDiagnosticFailure.SettingsControlsInvalid, result);
    }

    [Fact]
    public void ToProcessExitCode_FailureUsesBoundedNonSensitiveCode()
    {
        // Act
        var result = new UiDiagnosticResult(UiDiagnosticFailure.OverlayFocusMissing);

        // Assert
        Assert.Equal(19, result.ToProcessExitCode());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void ValidateCriticalControls_MissingHiddenOrUnnamedControl_FailsClosed(
        bool exists,
        bool isVisible,
        bool hasAutomationName)
    {
        // Act
        var result = UiDiagnosticContract.ValidateCriticalControls(
            [new UiDiagnosticControlSnapshot(exists, isVisible, hasAutomationName)]);

        // Assert
        Assert.Equal(UiDiagnosticFailure.SettingsControlsInvalid, result);
    }

    [Theory]
    [InlineData(0, 0, false, "None")]
    [InlineData(1, 0, false, "UnexpectedDependencyUse")]
    [InlineData(0, 1, false, "UnexpectedDependencyUse")]
    [InlineData(0, 0, true, "UnexpectedSettingsUse")]
    public void ValidateIsolation_ProhibitedDependencyOrSettingsPath_FailsClosed(
        int secretStoreCallCount,
        int audioCatalogCallCount,
        bool temporarySettingsWereCreated,
        string expectedFailureName)
    {
        // Act
        var result = UiDiagnosticContract.ValidateIsolation(
            secretStoreCallCount,
            audioCatalogCallCount,
            temporarySettingsWereCreated);

        // Assert
        Assert.Equal(expectedFailureName, result.ToString());
    }
}
