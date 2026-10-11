using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

namespace GuildChest.Build;

internal enum BuildConfiguration
{
    Debug,
    Release
}

internal sealed class Build : NukeBuild
{
    public static int Main()
    {
        // Keep automation noninteractive and prevent telemetry from rewriting the build project.
        Environment.SetEnvironmentVariable("NUKE_TELEMETRY_OPTOUT", "1");
        return Execute<Build>(build => build.Compile);
    }

    [Parameter("Build and test configuration (Debug or Release). Packaging always builds Release.")]
    private readonly BuildConfiguration configuration = BuildConfiguration.Debug;

    [Parameter("Refresh game references and pinned mod dependencies. Release and Compatibility always refresh.")]
    private readonly bool updateReferences = false;

    [Parameter("Use an existing Valheim Managed directory. Release and Compatibility require downloaded references.")]
    private readonly string? valheimManagedDir = null;

    [Parameter("Python executable used for reference setup, packaging, and Python tests.")]
    private readonly string python = "python3";

    [Parameter("Optional dotnet test logger, for example trx;LogFileName=tests.trx.")]
    private readonly string? testLogger = null;

    [Parameter("Optional directory for dotnet test results.")]
    private readonly string? testResultsDirectory = null;

    [Parameter("Release tag to validate against manifest.json and origin/main, for example v1.0.9.")]
    private readonly string? releaseTag = null;

    private string Solution => (RootDirectory / "GuildChest.sln").ToString();
    private string TestProject => (RootDirectory / "tests/GuildChest.Tests/GuildChest.Tests.csproj").ToString();
    private string PluginProject => (RootDirectory / "src/GuildChest/GuildChest.csproj").ToString();
    private string NativeProject => (RootDirectory / "tests/GuildChest.NativeSmoke/GuildChest.NativeSmoke.csproj").ToString();
    private string? ManagedDirectory => string.IsNullOrWhiteSpace(valheimManagedDir)
        ? null
        : Path.GetFullPath(valheimManagedDir, RootDirectory);

    private string Version
    {
        get
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(RootDirectory / "manifest.json"));
            return manifest.RootElement.GetProperty("version_number").GetString()!;
        }
    }

    private bool FreshReferences => updateReferences || InvokedTargets.Any(target =>
        target.Name.Equals(nameof(Release), StringComparison.OrdinalIgnoreCase) ||
        target.Name.Equals(nameof(Compatibility), StringComparison.OrdinalIgnoreCase));

    private Target Setup => target => target
        .Description("Download game references and pinned mod dependencies, reusing existing files by default.")
        .Executes(() =>
        {
            var arguments = new List<string>();
            if (FreshReferences)
                arguments.Add("--update");
            var managed = ManagedDirectory;
            if (managed != null)
                arguments.AddRange(new[] { "--valheim-managed-dir", managed });
            RunPython("setup.py", arguments.ToArray());
        });

    private Target Restore => target => target
        .Description("Prepare references and restore the product solution using NuGet lock files.")
        .DependsOn(Setup)
        .Executes(() => DotNetRestore(settings => settings
            .SetProjectFile(Solution)
            .EnableLockedMode()
            .SetProcessWorkingDirectory(RootDirectory)));

    private Target RestoreTests => target => target
        .Unlisted()
        .Executes(() => DotNetRestore(settings => settings
            .SetProjectFile(TestProject)
            .EnableLockedMode()
            .SetProcessWorkingDirectory(RootDirectory)));

    private Target Compile => target => target
        .Description("Prepare references, restore, and build the solution (Debug by default).")
        .DependsOn(Restore)
        .Before(CompileRelease)
        .Executes(() => DotNetBuild(BuildSettings(Solution, configuration)));

    private Target Test => target => target
        .Description("Run C# and Python tests without downloading game references.")
        .DependsOn(RestoreTests)
        .Before(Setup)
        .Executes(() =>
        {
            var settings = new DotNetTestSettings()
                .SetProjectFile(TestProject)
                .SetConfiguration(configuration.ToString())
                .EnableNoRestore()
                .SetProcessWorkingDirectory(RootDirectory);
            if (!string.IsNullOrWhiteSpace(testLogger))
                settings = settings.SetLoggers(testLogger);
            if (!string.IsNullOrWhiteSpace(testResultsDirectory))
                settings = settings.SetResultsDirectory(testResultsDirectory);
            DotNetTest(settings);
            RunPythonModule("unittest", "discover", "-s", "tests", "-p", "test_*.py");
        });

    private Target CompileRelease => target => target
        .Unlisted()
        .DependsOn(Restore)
        .Executes(() => DotNetBuild(BuildSettings(PluginProject, BuildConfiguration.Release)));

    private Target Package => target => target
        .Description("Build Release binaries and create the deterministic ZIP after checking the build receipt.")
        .DependsOn(CompileRelease)
        .Executes(() => RunPython("package.py"));

    private Target RestoreNativeSmoke => target => target
        .Unlisted()
        .DependsOn(Setup)
        .Executes(() => DotNetRestore(settings => settings
            .SetProjectFile(NativeProject)
            .EnableLockedMode()
            .SetProcessWorkingDirectory(RootDirectory)));

    private Target NativeSmoke => target => target
        .Description("Compile the optional native fixture in Release; this does not run gameplay checks.")
        .DependsOn(RestoreNativeSmoke)
        .After(Package)
        .Executes(() => DotNetBuild(BuildSettings(NativeProject, BuildConfiguration.Release)));

    private Target ValidateRelease => target => target
        .Description("Verify the manifest version, main-branch ancestry, and categorized release notes.")
        .Requires(() => !string.IsNullOrWhiteSpace(releaseTag))
        .Before(RestoreTests, Setup)
        .Executes(() =>
        {
            RunPython("check_release.py", releaseTag!);
            var output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
            if (!string.IsNullOrEmpty(output))
                File.AppendAllText(output, $"version={Version}{Environment.NewLine}");
        });

    private Target Checksum => target => target
        .Description("Build the package and write its SHA-256 checksum alongside the ZIP.")
        .DependsOn(Package)
        .Executes(() =>
        {
            var packageName = $"GuildChest-{Version}.zip";
            var package = RootDirectory / "artifacts" / packageName;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
            File.WriteAllText($"{package}.sha256", $"{hash}  {packageName}\n");
            Log.Information("Created checksum for {Package}", packageName);
        });

    private Target Release => target => target
        .Description("Validate the release tag, test, fetch fresh references, compile, and package Release with a checksum.")
        .Requires(() => string.IsNullOrWhiteSpace(valheimManagedDir))
        .DependsOn(ValidateRelease, Test, Compile, Checksum);

    private Target Compatibility => target => target
        .Description("Test against fresh references, compile, package Release, and compile the native fixture.")
        .Requires(() => string.IsNullOrWhiteSpace(valheimManagedDir))
        .DependsOn(Test, Compile, Package, NativeSmoke);

    private DotNetBuildSettings BuildSettings(string project, BuildConfiguration buildConfiguration)
    {
        var settings = new DotNetBuildSettings()
            .SetProjectFile(project)
            .SetConfiguration(buildConfiguration.ToString())
            .EnableNoRestore()
            .SetProcessWorkingDirectory(RootDirectory);
        var managed = ManagedDirectory;
        if (managed != null)
            settings = settings.SetProperty("ValheimManagedDir", managed);
        return settings;
    }

    private void RunPython(string script, params string[] arguments) =>
        RunPythonArguments(new[] { (RootDirectory / "scripts" / script).ToString() }.Concat(arguments).ToArray());

    private void RunPythonModule(string module, params string[] arguments) =>
        RunPythonArguments(new[] { "-m", module }.Concat(arguments).ToArray());

    private void RunPythonArguments(string[] arguments)
    {
        var command = new ProcessStartInfo(python)
        {
            WorkingDirectory = RootDirectory,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            command.ArgumentList.Add(argument);
        command.Environment["GUILDCHEST_BUILD_RUNNER"] = BuildAssemblyFile;
        Log.Information("> {Python} {Arguments}", python, string.Join(" ", arguments));
        using var process = Process.Start(command) ?? throw new InvalidOperationException("Could not start Python.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Python exited with code {process.ExitCode}.");
    }
}
