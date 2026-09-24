using System;
using System.Security.Cryptography;
using System.Text;

public interface IInterviewBridgeIdSource
{
    // One per bridge transport attempt; matches ^[A-Za-z0-9-]{8,64}$.
    string NewCorrelationId();

    // One per logical operation (the server idempotency key); matches ^[A-Za-z0-9_-]{1,64}$.
    string NewRequestId();
}

// Random, content-free identifiers: 128 bits from a cryptographic source, lowercase hex.
// Never derived from user, session, time, or conversation data.
public sealed class InterviewBridgeIdSource : IInterviewBridgeIdSource
{
    private readonly RandomNumberGenerator random = RandomNumberGenerator.Create();

    public string NewCorrelationId() => "uc-" + RandomHex();

    public string NewRequestId() => "ur-" + RandomHex();

    private string RandomHex()
    {
        var bytes = new byte[16];
        random.GetBytes(bytes);

        var builder = new StringBuilder(32);
        foreach (byte b in bytes)
            builder.Append(b.ToString("x2"));

        return builder.ToString();
    }
}

public interface IInterviewBridgeTransport
{
    // False when the transport is not enabled (no validated parent Blazor origin). Nothing is sent then.
    bool IsEnabled { get; }

    // Posts one serialized envelope to the single validated parent Blazor origin (never a wildcard).
    bool Post(string envelopeJson);

    // Raised with the raw JSON of a message that already passed the window.parent source and
    // parent-origin checks.
    event Action<string> Received;
}
