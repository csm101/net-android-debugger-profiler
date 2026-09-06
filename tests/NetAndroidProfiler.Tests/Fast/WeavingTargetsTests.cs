using System.Xml;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// The MSBuild targets file ships to users and is imported by their builds: a
/// malformed one fails their whole build (an XML comment containing "--" did
/// exactly that once), so it is validated here rather than on the next real build.
/// </summary>
public class WeavingTargetsTests
{
    private static string TargetsPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "build", "NetAndroidProfiler.Weaving.targets");
        }
    }

    [Fact]
    public void Targets_file_is_well_formed_xml()
    {
        Assert.True(File.Exists(TargetsPath), $"targets file missing: {TargetsPath}");
        var doc = new XmlDocument();
        doc.Load(TargetsPath);           // throws on malformed XML, including bad comments
        Assert.Equal("Project", doc.DocumentElement!.Name);
    }

    [Fact]
    public void Targets_only_weave_the_android_application_project()
    {
        string text = File.ReadAllText(TargetsPath);
        // Importing the file build-wide must leave referenced class libraries alone.
        Assert.Contains("'$(AndroidApplication)' == 'true'", text);
        Assert.Contains("NapWeave", text);
        Assert.Contains("NapCallspec", text);
    }
}
