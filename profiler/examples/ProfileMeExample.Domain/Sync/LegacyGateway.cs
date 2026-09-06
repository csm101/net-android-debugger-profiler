namespace ProfileMeExample.Domain.Sync;

public sealed record AccountBalance(decimal Amount, string Currency);

/// <summary>
/// A client written before async reached this code base: it waits for the "server" on
/// the calling thread. Called from a button handler, that thread is the UI thread.
/// </summary>
/// <remarks>
/// Defect on purpose: <see cref="FetchBalance"/> blocks for the whole round trip. In a
/// sampling profile the handler has many inclusive samples but almost no CPU samples:
/// the thread is waiting, not computing. The parse that follows is the only real CPU
/// work and shows the difference between the two columns.
/// </remarks>
public sealed class LegacyGateway
{
    private static readonly TimeSpan ServerRoundTrip = TimeSpan.FromMilliseconds(2500);

    public AccountBalance FetchBalance()
    {
        var payload = FetchPayloadAsync().GetAwaiter().GetResult();
        return Parse(payload);
    }

    private static async Task<string> FetchPayloadAsync()
    {
        await Task.Delay(ServerRoundTrip).ConfigureAwait(false);
        return "amount=1234.56;currency=EUR;checksum=" + new string('a', 4000);
    }

    /// <summary>Validates the payload's checksum the slow way, character by character, many times.</summary>
    private static AccountBalance Parse(string payload)
    {
        var fields = new Dictionary<string, string>();
        foreach (var pair in payload.Split(';'))
        {
            var separator = pair.IndexOf('=');
            fields[pair[..separator]] = pair[(separator + 1)..];
        }

        var checksum = 0;
        for (var round = 0; round < 2000; round++)
            foreach (var c in fields["checksum"])
                checksum = unchecked(checksum * 31 + c);

        if (checksum == int.MinValue)
            throw new InvalidDataException("corrupt payload");

        return new AccountBalance(decimal.Parse(fields["amount"], System.Globalization.CultureInfo.InvariantCulture), fields["currency"]);
    }
}
