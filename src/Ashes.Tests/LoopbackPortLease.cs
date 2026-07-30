using System.Net;
using System.Net.Sockets;

namespace Ashes.Tests;

/// <summary>
/// Coordinates loopback ports across parallel tests. Guest-server tests must release their probe
/// socket before the compiled process can bind, so the port remains reserved here until that
/// process is stopped. Listener-backed fixtures consult the same registry while binding port zero
/// and cannot claim a port during that handoff window.
/// </summary>
internal sealed class LoopbackPortLease : IDisposable
{
    private static readonly object Sync = new();
    private static readonly HashSet<int> ReservedPorts = [];
    private bool disposed;

    private LoopbackPortLease(int port)
    {
        Port = port;
    }

    public int Port { get; }

    public static LoopbackPortLease Create()
    {
        lock (Sync)
        {
            var rejectedProbes = new List<TcpListener>();
            try
            {
                while (true)
                {
                    TcpListener probe = StartEphemeralListener();
                    int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                    if (ReservedPorts.Add(port))
                    {
                        probe.Stop();
                        return new LoopbackPortLease(port);
                    }

                    // Keep rejected ports bound until another ephemeral port has been selected.
                    rejectedProbes.Add(probe);
                }
            }
            finally
            {
                foreach (TcpListener probe in rejectedProbes)
                {
                    probe.Stop();
                }
            }
        }
    }

    public static TcpListener StartListener()
    {
        lock (Sync)
        {
            var rejectedListeners = new List<TcpListener>();
            try
            {
                while (true)
                {
                    TcpListener listener = StartEphemeralListener();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    if (!ReservedPorts.Contains(port))
                    {
                        return listener;
                    }

                    // Keep rejected ports bound until another ephemeral port has been selected.
                    rejectedListeners.Add(listener);
                }
            }
            finally
            {
                foreach (TcpListener listener in rejectedListeners)
                {
                    listener.Stop();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (disposed)
            {
                return;
            }

            ReservedPorts.Remove(Port);
            disposed = true;
        }
    }

    private static TcpListener StartEphemeralListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }
}
