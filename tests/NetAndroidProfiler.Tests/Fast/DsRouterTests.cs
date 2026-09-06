using System.Net;
using System.Net.Sockets;
using NetAndroidProfiler.Core.Collection;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// dotnet-dsrouter 9.0.x has no port option, so one machine runs one router: a leftover
/// from an interrupted session silently owns the port, and every later session used to
/// fail minutes later with "no runtime connected" instead of naming the real cause.
/// </summary>
public class DsRouterTests
{
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
