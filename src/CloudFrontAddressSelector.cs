using System.Diagnostics;
using System.Net;

namespace CurseForgeProxy;

/// <summary>
/// Chooses the CloudFront edge address for each upstream attempt, steering attempts away from addresses that recently answered
/// "Request blocked" or failed to connect, and from addresses whose connection pool is already full.
/// </summary>
/// <remarks>
/// A full pool queues requests toward the attempt timeout, which ends in a 504 that is never retried, while a blocking address
/// answers quickly with a 403 that is retried elsewhere. Untried addresses therefore rank as: available, cooling down, full,
/// then both; ties keep the request's rotation order, and among cooling addresses the one that recovers first wins.
/// An address that failed to connect counts as full with a single attempt in flight until an attempt there gets a response,
/// so once its cool-down ends it is probed one attempt at a time instead of taking its share of the rotation.
/// </remarks>
internal sealed partial class CloudFrontAddressSelector(TimeProvider timeProvider, ILogger<CloudFrontAddressSelector> logger)
{
    public const int MaximumConnectionsPerAddress = 32;

    public static readonly TimeSpan AddressCooldown = TimeSpan.FromSeconds(seconds: 30);

    private static readonly IPAddress[] Addresses =
    [
        IPAddress.Parse(ipString: "2600:f0f0:601::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse(ipString: "2600:f0f0:602::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse(ipString: "2600:f0f0:603::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse(ipString: "2600:9000:2000::") // CloudFront / AWS global edge range
    ];

    private readonly int[] _activeAttempts = new int[Addresses.Length];
    private readonly long[] _blockedAttempts = new long[Addresses.Length];
    private readonly int[] _connectionFailureStreaks = new int[Addresses.Length];
    private readonly long[] _cooldownEnds = new long[Addresses.Length];
    private int _nextStartIndex;

    public static int AddressCount => Addresses.Length;

    public AddressLease Acquire(int startIndex, IReadOnlyCollection<IPAddress> triedAddresses)
    {
        int index = SelectIndex(startIndex, triedAddresses);
        int activeAttempts = Interlocked.Increment(ref _activeAttempts[index]);

        Debug.Assert(activeAttempts > 0, message: "Active attempts must stay positive while an address is leased.");

        return new AddressLease(this, index);
    }

    public int NextStartIndex()
    {
        int index = Interlocked.Increment(ref _nextStartIndex) - 1;
        int remainder = index % Addresses.Length;

        return remainder < 0 ? remainder + Addresses.Length : remainder;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CloudFront address {CloudFrontAddress} is blocking requests ({BlockedAttempts} blocked attempts so far); new attempts avoid it for {CooldownSeconds} s."
    )]
    private static partial void LogAddressBlocked(ILogger logger, IPAddress cloudFrontAddress, long blockedAttempts, double cooldownSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "CloudFront address {CloudFrontAddress} is responding again after {ConnectionFailures} failed connection attempts.")]
    private static partial void LogAddressRecovered(ILogger logger, IPAddress cloudFrontAddress, int connectionFailures);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CloudFront address {CloudFrontAddress} is not accepting connections ({ConnectionFailures} failed connection attempts since its last response); new attempts avoid it for {CooldownSeconds} s, then probe it one at a time until it responds."
    )]
    private static partial void LogAddressUnreachable(ILogger logger, IPAddress cloudFrontAddress, int connectionFailures, double cooldownSeconds);

    private void RecordBlocked(int index)
    {
        long blockedAttempts = Interlocked.Increment(ref _blockedAttempts[index]);

        if (StartCooldown(index))
            LogAddressBlocked(logger, Addresses[index], blockedAttempts, AddressCooldown.TotalSeconds);
    }

    private void RecordConnectionFailure(int index)
    {
        int connectionFailures = Interlocked.Increment(ref _connectionFailureStreaks[index]);

        if (StartCooldown(index))
            LogAddressUnreachable(logger, Addresses[index], connectionFailures, AddressCooldown.TotalSeconds);
    }

    private void RecordResponse(int index)
    {
        int connectionFailures = Interlocked.Exchange(ref _connectionFailureStreaks[index], value: 0);

        if (connectionFailures > 0)
            LogAddressRecovered(logger, Addresses[index], connectionFailures);
    }

    private void Release(int index)
    {
        int activeAttempts = Interlocked.Decrement(ref _activeAttempts[index]);

        Debug.Assert(activeAttempts >= 0, message: "An address lease was released more than once.");
    }

    private int SelectIndex(int startIndex, IReadOnlyCollection<IPAddress> triedAddresses)
    {
        long now = timeProvider.GetUtcNow().UtcTicks;
        int selectedIndex = startIndex % Addresses.Length;
        int selectedRank = int.MaxValue;
        long selectedCooldownEnd = long.MaxValue;

        for (int offset = 0; offset < Addresses.Length; offset++)
        {
            int index = (startIndex + offset) % Addresses.Length;

            if (triedAddresses.Contains(Addresses[index]))
                continue;

            long cooldownEnd = Volatile.Read(ref _cooldownEnds[index]);
            bool isCoolingDown = cooldownEnd > now;
            int capacity = Volatile.Read(ref _connectionFailureStreaks[index]) > 0 ? 1 : MaximumConnectionsPerAddress;
            bool isFull = Volatile.Read(ref _activeAttempts[index]) >= capacity;
            int rank = (isFull ? 2 : 0) + (isCoolingDown ? 1 : 0);
            bool recoversSooner = rank == selectedRank && isCoolingDown && cooldownEnd < selectedCooldownEnd;

            if (rank < selectedRank || recoversSooner)
            {
                selectedIndex = index;
                selectedRank = rank;
                selectedCooldownEnd = cooldownEnd;
            }
        }

        return selectedIndex;
    }

    private bool StartCooldown(int index)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        long previousCooldownEnd = Interlocked.Exchange(ref _cooldownEnds[index], (now + AddressCooldown).UtcTicks);

        return previousCooldownEnd <= now.UtcTicks;
    }

    /// <summary>
    /// Holds one in-flight upstream attempt against a CloudFront address and releases it when disposed.
    /// </summary>
    internal sealed class AddressLease(CloudFrontAddressSelector selector, int index) : IDisposable
    {
        public IPAddress Address => Addresses[index];

        public void Dispose()
        {
            selector.Release(index);
        }

        public void RecordBlocked()
        {
            selector.RecordBlocked(index);
        }

        public void RecordConnectionFailure()
        {
            selector.RecordConnectionFailure(index);
        }

        public void RecordResponse()
        {
            selector.RecordResponse(index);
        }
    }
}
