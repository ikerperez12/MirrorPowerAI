namespace MirrorPowerAI.Benchmark.Tests;

public sealed class CorpusBenchmarkScriptContractTests
{
    [Fact]
    public void StableCorpusWrapper_NeverRestoresAndDisablesSdkAdvertisingChecks()
    {
        var script = File.ReadAllText(FindRepositoryFile("Windows", "benchmark-corpus.ps1"));

        Assert.DoesNotContain("'restore'", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$NoRestore", script, StringComparison.Ordinal);
        Assert.Contains("'build', $project, '-c', 'Release', '--no-restore'", script, StringComparison.Ordinal);
        Assert.Contains("'--no-build',", script, StringComparison.Ordinal);
        Assert.Contains("'--no-restore',", script, StringComparison.Ordinal);
        Assert.Contains("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", script, StringComparison.Ordinal);
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

        throw new FileNotFoundException(
            "The stable corpus benchmark wrapper was not found.",
            Path.Combine(relativePath));
    }
}
