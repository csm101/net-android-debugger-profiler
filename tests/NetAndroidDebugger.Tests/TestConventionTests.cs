using System.Reflection;
using NetAndroidDebugger.Tests.Harness;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// The suite's own rules, checked by the suite.
/// </summary>
public sealed class TestConventionTests
{
    /// <summary>
    /// A class that takes the device fixture needs a device, and has to say so in its
    /// category or `--filter "Category!=Device"` runs it anyway. That is not a formality:
    /// without the trait these tests launched against whatever phone happened to be plugged
    /// in, took a quarter of an hour and failed 58 times, and the failures said nothing
    /// about the code. The convention is the one NetAndroid.Device.Tests already follows.
    /// </summary>
    [Fact]
    public void Every_class_that_needs_a_device_says_so_in_its_category()
    {
        var offenders = typeof(TestConventionTests).Assembly.GetTypes()
            .Where(UsesTheDeviceCollection)
            .Where(t => !IsMarkedAsDevice(t))
            .Select(t => t.Name)
            .OrderBy(name => name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "these classes take the device fixture but carry no [Trait(\"Category\", \"Device\")], "
            + "so a device-free run executes them: " + string.Join(", ", offenders));
    }

    private static bool UsesTheDeviceCollection(Type type) =>
        type.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(CollectionAttribute)
            && a.ConstructorArguments.Count == 1
            && (a.ConstructorArguments[0].Value as string) == DeviceCollection.Name);

    private static bool IsMarkedAsDevice(Type type) =>
        type.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(TraitAttribute)
            && a.ConstructorArguments.Count == 2
            && (a.ConstructorArguments[0].Value as string) == "Category"
            && (a.ConstructorArguments[1].Value as string) == "Device");
}
