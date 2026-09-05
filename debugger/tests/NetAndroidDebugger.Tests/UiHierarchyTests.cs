using NetAndroidDebugger.Core.Device;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// Reading a uiautomator dump and the small pure helpers of <see cref="DeviceControl"/>. No device:
/// this is about the XML shape adb hands back, the noise a vendor prints before it, and the
/// quoting that keeps a text or a key name intact through `adb shell input`.
/// </summary>
public sealed class UiHierarchyTests
{
    private const string SampleDump =
        """
        <?xml version='1.0' encoding='UTF-8' standalone='yes' ?><hierarchy rotation="0"><node index="0" text="" resource-id="" class="android.widget.FrameLayout" package="net.androiddebugger.testtarget" content-desc="" checkable="false" checked="false" clickable="false" enabled="true" focusable="false" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[0,0][1080,2340]"><node index="0" text="Counter: 0" resource-id="net.androiddebugger.testtarget:id/counter_label" class="android.widget.TextView" package="net.androiddebugger.testtarget" content-desc="" checkable="false" checked="false" clickable="false" enabled="true" focusable="false" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[470,1100][610,1160]" /><node index="1" text="INCREMENT" resource-id="net.androiddebugger.testtarget:id/increment_button" class="android.widget.Button" package="net.androiddebugger.testtarget" content-desc="" checkable="false" checked="false" clickable="true" enabled="true" focusable="true" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[400,1160][680,1300]" /></node></hierarchy>
        """;

    [Fact]
    public void Parse_ReadsNodes_WithBoundsAndFlags()
    {
        var tree = UiHierarchy.Parse(SampleDump);

        Assert.Equal(0, tree.Rotation);
        var all = UiHierarchy.Flatten(tree.Root).ToList();
        Assert.Equal(3, all.Count);
        var button = all.Single(n => n.ShortResourceId == "increment_button");
        Assert.Equal("Button", button.ShortClassName);
        Assert.Equal("android.widget.Button", button.ClassName);
        Assert.True(button.Clickable);
        Assert.Equal(new UiBounds(400, 1160, 680, 1300), button.Bounds);
        Assert.Equal(540, button.Bounds.CenterX);
        Assert.Equal(1230, button.Bounds.CenterY);
        Assert.Equal(1, button.Depth);
        Assert.False(all[0].IsInteresting);
        Assert.True(button.IsInteresting);
    }

    [Fact]
    public void Parse_SkipsTheNoise_AVendorPrintsBeforeTheXml()
    {
        // MIUI's uiautomator prints a stack trace about a theme file before the dump.
        var noisy = "java.io.FileNotFoundException: /data/system/theme_config/theme_compatibility.xml: open failed\n\tat libcore.io.IoBridge.open(IoBridge.java:492)\n" + SampleDump;
        var tree = UiHierarchy.Parse(noisy);
        Assert.Equal(3, UiHierarchy.Flatten(tree.Root).Count());
    }

    [Fact]
    public void Parse_RejectsOutputWithoutAHierarchy()
    {
        var ex = Assert.Throws<DeviceControlException>(() => UiHierarchy.Parse("ERROR: could not get idle state."));
        Assert.Contains("hierarchy", ex.Message);
    }

    [Fact]
    public void Find_MatchesShortIds_ShortClasses_AndTextCaseInsensitively()
    {
        var root = UiHierarchy.Parse(SampleDump).Root;

        Assert.Single(UiHierarchy.Find(root, new UiSelector(ResourceId: "increment_button")));
        Assert.Single(UiHierarchy.Find(root, new UiSelector(ResourceId: "net.androiddebugger.testtarget:id/increment_button")));
        Assert.Empty(UiHierarchy.Find(root, new UiSelector(ResourceId: "other:id/increment_button")));
        Assert.Single(UiHierarchy.Find(root, new UiSelector(Text: "increment")));
        Assert.Single(UiHierarchy.Find(root, new UiSelector(TextContains: "counter")));
        Assert.Single(UiHierarchy.Find(root, new UiSelector(ClassName: "Button")));
        Assert.Single(UiHierarchy.Find(root, new UiSelector(Clickable: true)));
        Assert.Equal(3, UiHierarchy.Find(root, new UiSelector()).Count);
        Assert.Empty(UiHierarchy.Find(root, new UiSelector(Text: "increment", ClassName: "TextView")));
    }

    [Fact]
    public void Parse_WrapsSeveralTopLevelWindows_InOneRoot()
    {
        var two = SampleDump.Replace("</hierarchy>",
            """<node index="0" text="OK" resource-id="" class="android.widget.Button" package="p" content-desc="" checkable="false" checked="false" clickable="true" enabled="true" focusable="true" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[100,100][200,200]" /></hierarchy>""");
        var tree = UiHierarchy.Parse(two);
        Assert.Equal(2, tree.Root.Children.Count);
        Assert.Equal(new UiBounds(0, 0, 1080, 2340), tree.Root.Bounds);
        Assert.Equal(4, UiHierarchy.Find(tree.Root, new UiSelector()).Count - 1);
    }

    [Theory]
    [InlineData("back", "KEYCODE_BACK")]
    [InlineData("KEYCODE_ENTER", "KEYCODE_ENTER")]
    [InlineData("volume up", "KEYCODE_VOLUME_UP")]
    [InlineData("66", "66")]
    public void NormalizeKey_AcceptsNames_WithOrWithoutPrefix_AndCodes(string given, string expected)
        => Assert.Equal(expected, DeviceControl.NormalizeKey(given));

    [Fact]
    public void QuoteForInputText_EscapesSpaces_AndShellQuotes()
    {
        Assert.Equal("'hello%sworld'", DeviceControl.QuoteForInputText("hello world"));
        Assert.Equal("'it'\\''s'", DeviceControl.QuoteForInputText("it's"));
    }

    [Fact]
    public void LooksLikeInjectionDenied_RecognisesTheVendorRefusal()
    {
        Assert.True(DeviceControl.LooksLikeInjectionDenied(
            "java.lang.SecurityException: Injecting to another application requires INJECT_EVENTS permission"));
        Assert.False(DeviceControl.LooksLikeInjectionDenied("Error: Invalid arguments for command: tap"));
    }

    [Fact]
    public void ReadPngDimensions_ReadsTheHeader_AndRejectsOtherBytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x04, 0x38, 0, 0, 0x09, 0x24 };
        Assert.Equal((1080, 2340), DeviceControl.ReadPngDimensions(png));
        Assert.Throws<DeviceControlException>(() => DeviceControl.ReadPngDimensions("not a png at all, really"u8));
        Assert.Throws<DeviceControlException>(() => DeviceControl.ReadPngDimensions([]));
    }
}
