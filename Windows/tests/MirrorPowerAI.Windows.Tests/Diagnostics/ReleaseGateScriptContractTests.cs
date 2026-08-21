namespace MirrorPowerAI.Windows.Tests.Diagnostics;

public sealed class ReleaseGateScriptContractTests
{
    [Fact]
    public void ReleaseGate_InvokesLocalUiAndProvenanceChecksAfterPublishing()
    {
        // Arrange
        var buildScript = File.ReadAllText(FindRepositoryFile("Windows", "build.ps1"));
        var overlayIndex = buildScript.IndexOf("verify-overlay.ps1", StringComparison.Ordinal);
        var shellIndex = buildScript.IndexOf("verify-shell.ps1", StringComparison.Ordinal);
        var uiIndex = buildScript.IndexOf("verify-ui.ps1", StringComparison.Ordinal);
        var provenanceIndex = buildScript.IndexOf("verify-provenance.ps1", StringComparison.Ordinal);

        // Assert
        Assert.True(overlayIndex >= 0, "ReleaseGate must verify a real protected overlay.");
        Assert.True(shellIndex > overlayIndex, "Shell validation must run after overlay validation.");
        Assert.True(uiIndex > shellIndex, "UI lifecycle validation must run after shell validation.");
        Assert.True(provenanceIndex > uiIndex, "Provenance must be checked after all candidate diagnostics.");
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The repository build script was not found.", Path.Combine(relativePath));
    }
}
