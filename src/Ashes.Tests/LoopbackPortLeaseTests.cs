using System.Net;
using System.Net.Sockets;
using Shouldly;

namespace Ashes.Tests;

public sealed class LoopbackPortLeaseTests
{
    [Test]
    public void ListenerFixture_DoesNotClaimPortReservedForGuestServerHandoff()
    {
        using LoopbackPortLease guestPortLease = LoopbackPortLease.Create();
        using TcpListener fixtureListener = LoopbackPortLease.StartListener();

        int fixturePort = ((IPEndPoint)fixtureListener.LocalEndpoint).Port;
        fixturePort.ShouldNotBe(guestPortLease.Port);

        using var guestListener = new TcpListener(IPAddress.Loopback, guestPortLease.Port);
        guestListener.Start();
        ((IPEndPoint)guestListener.LocalEndpoint).Port.ShouldBe(guestPortLease.Port);
    }

    [Test]
    public async Task ParallelReservationsAndListeners_HaveDistinctActivePorts()
    {
        const int count = 32;
        var guestPortLeases = new LoopbackPortLease?[count];
        var fixtureListeners = new TcpListener?[count];

        try
        {
            await Task.WhenAll(
                Task.Run(() =>
                {
                    for (int index = 0; index < count; index++)
                    {
                        guestPortLeases[index] = LoopbackPortLease.Create();
                    }
                }),
                Task.Run(() =>
                {
                    for (int index = 0; index < count; index++)
                    {
                        fixtureListeners[index] = LoopbackPortLease.StartListener();
                    }
                }));

            var activePorts = new HashSet<int>();
            for (int index = 0; index < count; index++)
            {
                LoopbackPortLease guestPortLease = guestPortLeases[index]
                    ?? throw new InvalidOperationException("Missing guest port lease.");
                activePorts.Add(guestPortLease.Port).ShouldBeTrue();

                TcpListener fixtureListener = fixtureListeners[index]
                    ?? throw new InvalidOperationException("Missing fixture listener.");
                int fixturePort = ((IPEndPoint)fixtureListener.LocalEndpoint).Port;
                activePorts.Add(fixturePort).ShouldBeTrue();
            }
        }
        finally
        {
            for (int index = 0; index < count; index++)
            {
                fixtureListeners[index]?.Stop();
                guestPortLeases[index]?.Dispose();
            }
        }
    }
}
