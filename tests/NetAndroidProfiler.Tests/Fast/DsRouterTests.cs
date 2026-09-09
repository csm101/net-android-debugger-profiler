using System.Net;
using System.Net.Sockets;
using NetAndroidProfiler.Core.Collection;
using NetAndroid.Device;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// dotnet-dsrouter 9.0.x has no port option, so one machine runs one router: a leftover
/// from an interrupted session silently owns the port, and every later session used to
/// fail minutes later with "no runtime connected" instead of naming the real cause.
/// </summary>
public class DsRouterTests
{
    /// <summary>
    /// dsrouter needs two ports on a physical device - the one the app connects to and the
    /// one the trace is read from - and until 2026-09-09 only the first was checked. A busy
    /// second one surfaced as dsrouter's own "only one usage of each socket address is
    /// normally permitted", which names neither the port nor its purpose; it cost a
    /// verification run to work out that Docker was sitting on 9001.
    /// </summary>
    [Fact]
    public async Task Either_busy_port_is_reported_with_the_port_and_what_it_is_for()
    {
        // Hold the port ourselves when it is free; when something else already holds it -
        // which is the very situation this guards against - that does just as well.
        TcpListener? listener = new(IPAddress.Loopback, DsRouterProcess.DeviceHostPort);
        try { listener.Start(); }
        catch (SocketException) { listener = null; }
        try
        {
            var e = await Assert.ThrowsAsync<ToolException>(
                () => DsRouterProcess.StartAsync(isEmulator: false, CancellationToken.None));

            Assert.Contains(DsRouterProcess.DeviceHostPort.ToString(), e.Message);
            Assert.Contains("the trace is read from", e.Message);
        }
        finally { listener?.Stop(); }
    }

    [Fact]
    public void A_listening_port_is_detected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(DsRouterProcess.IsPortInUse(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void A_free_port_is_not_reported_as_in_use()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();          // the port was free a moment ago and nothing else claimed it
        Assert.False(DsRouterProcess.IsPortInUse(port));
    }
}
