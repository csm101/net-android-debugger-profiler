using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Launch;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// Finding the app to launch in a tree. No device: this is about telling an Android application
/// apart from the thirty Android libraries around it, and about never picking one of several.
/// </summary>
public sealed class AppProjectFinderTests
{
    private sealed class TempTree : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nad-proj-");

        public string Root => _root.FullName;

        /// <summary>An Android application: it declares an ApplicationId.</summary>
        public string App(string relativeDir, string applicationId, string framework = "net9.0-android35.0") =>
            Project(relativeDir, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{framework}</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <ApplicationId>{applicationId}</ApplicationId>
                  </PropertyGroup>
                </Project>
                """);

        /// <summary>An Android library: same framework, no application identity.</summary>
        public string Library(string relativeDir) =>
            Project(relativeDir, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0-android35.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

        public string Project(string relativeDir, string xml)
        {
            var dir = Path.Combine(Root, relativeDir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Path.GetFileName(dir) + ".csproj");
            File.WriteAllText(path, xml);
            return path;
        }

        public string Solution(string name, params string[] projectPaths)
        {
            var path = Path.Combine(Root, name);
            var lines = projectPaths.Select(p =>
                $$"""Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "{{Path.GetFileNameWithoutExtension(p)}}", "{{Path.GetRelativePath(Root, p)}}", "{FFFFFFFF-0000-0000-0000-000000000001}" """);
            File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" + string.Join("\r\nEndProject\r\n", lines) + "\r\nEndProject\r\n");
            return path;
        }

        public void Dispose() => _root.Delete(recursive: true);
    }

    /// <summary>
    /// The whole point of the filter. In the reference application thirty-odd projects target an Android framework
    /// and two are applications, so the framework alone would offer thirty things to launch.
    /// </summary>
    [Fact]
    public void AndroidLibraries_AreNotLaunchable_OnlyApplicationsAre()
    {
        using var tree = new TempTree();
        tree.Library("Barcode.Droid");
        tree.Library("Bluetooth.Droid");
        tree.App("App.Droid", "com.example.app");

        var found = AppProjectFinder.Find(tree.Root);

        Assert.Single(found);
        Assert.Equal("com.example.app", found[0].ApplicationId);
        Assert.Equal("net9.0-android35.0", found[0].TargetFramework);
    }

    [Fact]
    public void NonAndroidProjects_AreNotLaunchable()
    {
        using var tree = new TempTree();
        tree.Project("Backend", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);

        Assert.Empty(AppProjectFinder.Find(tree.Root));
    }

    [Fact]
    public void AProjectTargetingSeveralFrameworks_IsFoundByItsAndroidOne()
    {
        using var tree = new TempTree();
        tree.Project("Shared", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net9.0-android35.0;net9.0-ios</TargetFrameworks>
                <ApplicationId>com.example.multi</ApplicationId>
              </PropertyGroup>
            </Project>
            """);

        var found = AppProjectFinder.Find(tree.Root);

        Assert.Single(found);
        Assert.Equal("net9.0-android35.0", found[0].TargetFramework);
    }

    /// <summary>
    /// Launching the wrong app looks exactly like the debugger not working, so several candidates
    /// are a question for the caller, not a coin toss.
    /// </summary>
    [Fact]
    public void SeveralApplications_FailWithTheList_RatherThanAGuess()
    {
        using var tree = new TempTree();
        tree.App("App.Droid", "com.example.app");
        tree.App("ExternalTools/Helper", "com.example.helper");

        Assert.Equal(2, AppProjectFinder.Find(tree.Root).Count);

        var ex = Assert.Throws<LaunchException>(() => AppProjectFinder.Single(tree.Root));
        Assert.Contains("com.example.app", ex.Message);
        Assert.Contains("com.example.helper", ex.Message);
    }

    [Fact]
    public void NoApplicationAtAll_SaysWhatMakesOneLaunchable()
    {
        using var tree = new TempTree();
        tree.Library("Barcode.Droid");

        var ex = Assert.Throws<LaunchException>(() => AppProjectFinder.Single(tree.Root));

        Assert.Contains("ApplicationId", ex.Message);
    }

    /// <summary>
    /// Pointing at the solution is how an ambiguous tree is disambiguated, so the solution must
    /// really narrow the search to what it names.
    /// </summary>
    [Fact]
    public void ASolution_NarrowsTheSearchToTheProjectsItNames()
    {
        using var tree = new TempTree();
        var app = tree.App("App.Droid", "com.example.app");
        tree.App("ExternalTools/Helper", "com.example.helper");
        var solution = tree.Solution("Product.sln", app);

        var found = AppProjectFinder.Find(solution);

        Assert.Single(found);
        Assert.Equal("com.example.app", found[0].ApplicationId);
        Assert.True(found[0].InSolution);
        // And now there is exactly one thing to launch.
        Assert.Equal("com.example.app", AppProjectFinder.Single(solution).ApplicationId);
    }

    [Fact]
    public void ASlnxSolution_IsReadToo()
    {
        using var tree = new TempTree();
        var app = tree.App("App.Droid", "com.example.app");
        var solution = Path.Combine(tree.Root, "Product.slnx");
        File.WriteAllText(solution, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="{Path.GetRelativePath(tree.Root, app)}" />
              </Folder>
            </Solution>
            """);

        Assert.Equal("com.example.app", AppProjectFinder.Single(solution).ApplicationId);
    }

    /// <summary>
    /// Walking a folder meets projects that merely sit next to the product. Marking the ones a
    /// solution names, and listing them first, is what makes the answer readable.
    /// </summary>
    [Fact]
    public void ProjectsASolutionNames_ComeFirst()
    {
        using var tree = new TempTree();
        var app = tree.App("ZZZ.Droid", "com.example.app");
        tree.App("AAA.Tool", "com.example.tool");
        tree.Solution("Product.sln", app);

        var found = AppProjectFinder.Find(tree.Root);

        Assert.Equal(2, found.Count);
        Assert.Equal("com.example.app", found[0].ApplicationId);
        Assert.True(found[0].InSolution);
        Assert.False(found[1].InSolution);
    }

    /// <summary>
    /// An app whose ApplicationId lives in a props file is still launchable — it is an Exe — but
    /// the package name has to be given, and the listing has to say so rather than showing a blank.
    /// </summary>
    [Fact]
    public void AnExeWithoutApplicationId_IsListed_AndSaysTheIdIsMissing()
    {
        using var tree = new TempTree();
        tree.Project("App.Droid", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0-android35.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);

        var found = AppProjectFinder.Single(tree.Root);

        Assert.Null(found.ApplicationId);
        Assert.Contains("no ApplicationId", AppProjectFinder.Line(found));
    }

    [Fact]
    public void BuildOutputIsNotWalked()
    {
        using var tree = new TempTree();
        tree.App("App.Droid", "com.example.app");
        tree.App("App.Droid/obj/Release/net9.0-android35.0/generated", "com.example.generated");

        var found = AppProjectFinder.Find(tree.Root);

        Assert.Single(found);
        Assert.Equal("com.example.app", found[0].ApplicationId);
    }

    [Fact]
    public void APathThatIsNeitherSolutionNorProject_SaysSo()
    {
        using var tree = new TempTree();

        Assert.Throws<LaunchException>(() => AppProjectFinder.Find(Path.Combine(tree.Root, "nowhere")));
    }

    /// <summary>This repository is a real tree with real apps in it (this TestTarget, the profiler's, the example app) and a lot of noise around them.</summary>
    [Fact]
    public void ThisRepository_YieldsTestTarget()
    {
        var found = AppProjectFinder.Find(Harness.TestEnvironment.RepoRoot);

        var app = Assert.Single(found, p => p.ApplicationId == Harness.TestEnvironment.TestTargetPackage);
        Assert.Equal(Harness.TestEnvironment.TestTargetPackage, app.ApplicationId);
    }
}

/// <summary>
/// Choosing the device when the caller named none. Pure, so every case is testable without
/// arranging hardware — including the two-devices case that is the whole reason this exists.
/// </summary>
public sealed class DeviceChoiceTests
{
    private static DeviceInfo Ready(string serial) => new(serial, "device", "Pixel", serial.StartsWith("emulator-"));

    [Fact]
    public void OneReadyDevice_IsNotAChoice()
    {
        Assert.Equal("emulator-5554", DebugSession.ChooseDevice([Ready("emulator-5554")]).Serial);
    }

    /// <summary>
    /// Two emulators online is the normal state on this machine — one of them belongs to another
    /// tool — so deducing here would silently debug on somebody else's device.
    /// </summary>
    [Fact]
    public void SeveralReadyDevices_FailListingThem()
    {
        var ex = Assert.Throws<LaunchException>(() =>
            DebugSession.ChooseDevice([Ready("emulator-5554"), Ready("emulator-5556")]));

        Assert.Contains("emulator-5554", ex.Message);
        Assert.Contains("emulator-5556", ex.Message);
    }

    [Fact]
    public void ADeviceThatIsNotReady_IsNotACandidate()
    {
        var devices = new DeviceInfo[] { Ready("emulator-5554"), new("RF8N", "unauthorized", null, false) };

        Assert.Equal("emulator-5554", DebugSession.ChooseDevice(devices).Serial);
    }

    [Fact]
    public void NothingReady_SaysWhatIsAttached()
    {
        var ex = Assert.Throws<LaunchException>(() =>
            DebugSession.ChooseDevice([new DeviceInfo("RF8N", "offline", null, false)]));

        Assert.Contains("RF8N", ex.Message);
        Assert.Contains("offline", ex.Message);
    }

    [Fact]
    public void NoDeviceAtAll_SaysToStartOne()
    {
        var ex = Assert.Throws<LaunchException>(() => DebugSession.ChooseDevice([]));

        Assert.Contains("No device is attached", ex.Message);
    }

    /// <summary>
    /// A serial that is not attached is the typical stale copy from an earlier session; listing
    /// what *is* attached turns it into a one-step fix.
    /// </summary>
    [Fact]
    public void ARequestedSerialThatIsNotAttached_ListsWhatIs()
    {
        var ex = Assert.Throws<LaunchException>(() =>
            DebugSession.ChooseDevice([Ready("emulator-5554")], "emulator-5560"));

        Assert.Contains("emulator-5560", ex.Message);
        Assert.Contains("emulator-5554", ex.Message);
    }

    /// <summary>A named device is used even when it is the only one, and even when it is not ready:
    /// the caller said which one, and a state check belongs to the launch, not to the choice.</summary>
    [Fact]
    public void ANamedDevice_IsUsedAsGiven()
    {
        Assert.Equal("RF8N", DebugSession.ChooseDevice([Ready("emulator-5554"), new DeviceInfo("RF8N", "device", null, false)], "RF8N").Serial);
    }

    /// <summary>
    /// A serial that was named but is not attached names where it came from, because "which of the
    /// three places do I fix" is the actual question - a variable left over from an earlier session
    /// looks exactly like a wrong config field.
    /// </summary>
    [Fact]
    public void ASerialThatIsNotAttached_NamesWhereItCameFrom()
    {
        var ex = Assert.Throws<LaunchException>(() =>
            DebugSession.ChooseDevice([Ready("emulator-5554")], "emulator-5560", "NAD_DEVICE_SERIAL"));

        Assert.Contains("emulator-5560", ex.Message);
        Assert.Contains("NAD_DEVICE_SERIAL", ex.Message);
        Assert.Contains("emulator-5554", ex.Message);
    }
}
