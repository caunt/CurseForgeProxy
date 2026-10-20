using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

namespace CurseForgeProxy.Tests;

/// <summary>
/// Checks how upstream attempts are spread across CloudFront edge addresses when some of them block requests or run out of pooled connections.
/// </summary>
public sealed class CloudFrontAddressSelectorTests
{
    /// <summary>
    /// After a "Request blocked" answer, every rotation start avoids that address for the cool-down, then uses it again.
    /// </summary>
    [Fact]
    public void CloudFrontAddressSelectorAvoidsBlockedAddressUntilCooldownEnds()
    {
        ManualTimeProvider timeProvider = new();
        CloudFrontAddressSelector selector = new(timeProvider, NullLogger<CloudFrontAddressSelector>.Instance);
        IPAddress blockedAddress = AcquireAndBlock(selector, startIndex: 0);

        for (int startIndex = 0; startIndex < CloudFrontAddressSelector.AddressCount; startIndex++)
        {
            using CloudFrontAddressSelector.AddressLease lease = selector.Acquire(startIndex, []);

            Assert.NotEqual(blockedAddress, lease.Address);
        }

        timeProvider.Advance(CloudFrontAddressSelector.AddressCooldown);

        using CloudFrontAddressSelector.AddressLease recoveredLease = selector.Acquire(startIndex: 0, []);

        Assert.Equal(blockedAddress, recoveredLease.Address);
    }

    /// <summary>
    /// After a connection failure, as in issue #105, the address is avoided for the cool-down, then takes one probe at a time until an attempt there gets a response.
    /// </summary>
    [Fact]
    public void CloudFrontAddressSelectorProbesUnreachableAddressOneAttemptAtATime()
    {
        ManualTimeProvider timeProvider = new();
        CloudFrontAddressSelector selector = new(timeProvider, NullLogger<CloudFrontAddressSelector>.Instance);
        IPAddress unreachableAddress = AcquireAndFailConnection(selector, startIndex: 0);

        using (CloudFrontAddressSelector.AddressLease coolingLease = selector.Acquire(startIndex: 0, []))
            Assert.NotEqual(unreachableAddress, coolingLease.Address);

        timeProvider.Advance(CloudFrontAddressSelector.AddressCooldown);

        using CloudFrontAddressSelector.AddressLease probeLease = selector.Acquire(startIndex: 0, []);

        Assert.Equal(unreachableAddress, probeLease.Address);

        using (CloudFrontAddressSelector.AddressLease concurrentLease = selector.Acquire(startIndex: 0, []))
            Assert.NotEqual(unreachableAddress, concurrentLease.Address);

        probeLease.RecordResponse();

        using CloudFrontAddressSelector.AddressLease recoveredLease = selector.Acquire(startIndex: 0, []);

        Assert.Equal(unreachableAddress, recoveredLease.Address);
    }

    /// <summary>
    /// With three addresses blocking, as in issue #100, new requests go to the fourth, and a retry that already tried it falls back to the blocked address whose cool-down ends first.
    /// </summary>
    [Fact]
    public void CloudFrontAddressSelectorRoutesAroundBlockedAddresses()
    {
        ManualTimeProvider timeProvider = new();
        CloudFrontAddressSelector selector = new(timeProvider, NullLogger<CloudFrontAddressSelector>.Instance);
        List<IPAddress> blockedAddresses = [];

        for (int block = 0; block < CloudFrontAddressSelector.AddressCount - 1; block++)
        {
            blockedAddresses.Add(AcquireAndBlock(selector, startIndex: 0));
            timeProvider.Advance(TimeSpan.FromSeconds(seconds: 1));
        }

        using CloudFrontAddressSelector.AddressLease firstAttempt = selector.Acquire(startIndex: 0, []);

        Assert.DoesNotContain(firstAttempt.Address, blockedAddresses);

        using CloudFrontAddressSelector.AddressLease retry = selector.Acquire(startIndex: 0, [firstAttempt.Address]);

        Assert.Equal(blockedAddresses[index: 0], retry.Address);
    }

    /// <summary>
    /// An address whose pool already holds the maximum number of connections is passed over while another address has room, and is chosen again once its attempts finish.
    /// </summary>
    [Fact]
    public void CloudFrontAddressSelectorSkipsAddressWithFullPool()
    {
        CloudFrontAddressSelector selector = new(new ManualTimeProvider(), NullLogger<CloudFrontAddressSelector>.Instance);
        List<IPAddress> otherAddresses = [];

        for (int startIndex = 1; startIndex < CloudFrontAddressSelector.AddressCount; startIndex++)
        {
            using CloudFrontAddressSelector.AddressLease lease = selector.Acquire(startIndex, []);

            otherAddresses.Add(lease.Address);
        }

        List<CloudFrontAddressSelector.AddressLease> busyLeases = [];

        for (int connection = 0; connection < CloudFrontAddressSelector.MaximumConnectionsPerAddress; connection++)
            busyLeases.Add(selector.Acquire(startIndex: 0, otherAddresses));

        IPAddress busyAddress = busyLeases[index: 0].Address;

        using (CloudFrontAddressSelector.AddressLease overflowLease = selector.Acquire(startIndex: 0, []))
            Assert.NotEqual(busyAddress, overflowLease.Address);

        foreach (CloudFrontAddressSelector.AddressLease busyLease in busyLeases)
            busyLease.Dispose();

        using CloudFrontAddressSelector.AddressLease freedLease = selector.Acquire(startIndex: 0, []);

        Assert.Equal(busyAddress, freedLease.Address);
    }

    private static IPAddress AcquireAndBlock(CloudFrontAddressSelector selector, int startIndex)
    {
        using CloudFrontAddressSelector.AddressLease lease = selector.Acquire(startIndex, []);

        lease.RecordBlocked();

        return lease.Address;
    }

    private static IPAddress AcquireAndFailConnection(CloudFrontAddressSelector selector, int startIndex)
    {
        using CloudFrontAddressSelector.AddressLease lease = selector.Acquire(startIndex, []);

        lease.RecordConnectionFailure();

        return lease.Address;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(year: 2026, month: 10, day: 18, hour: 0, minute: 0, second: 0, TimeSpan.Zero);

        public void Advance(TimeSpan duration)
        {
            _now += duration;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }
}
