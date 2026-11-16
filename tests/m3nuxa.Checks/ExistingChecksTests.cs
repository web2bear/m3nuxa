using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

[TestClass]
public sealed class ExistingChecksTests
{
    [TestMethod]
    public async Task ExistingConsoleChecksPass()
    {
        var projectRoot = FindProjectRoot();
        var projectPath = Path.Combine(projectRoot, "tests", "m3nuxa.Checks", "m3nuxa.Checks.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--property");
        startInfo.ArgumentList.Add("Platform=x64");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);

        using var process = Process.Start(startInfo)
            ?? throw new AssertFailedException("Could not start the existing check harness.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new AssertFailedException("The existing check harness exceeded the two-minute timeout.");
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.AreEqual(0, process.ExitCode, $"Check harness failed.\n{output}\n{error}");
        StringAssert.Contains(output, "All configuration and icon checks passed.");
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "m3nuxa.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "tests", "m3nuxa.Checks", "m3nuxa.Checks.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new AssertFailedException("Could not locate the repository root from the test output directory.");
    }
}
