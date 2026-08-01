namespace RainBird.Protocol;

/// <summary>Something on the wire did not match the protocol.</summary>
public class RainBirdProtocolException : Exception
{
    public RainBirdProtocolException(string message) : base(message) { }
    public RainBirdProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The controller password is wrong. The protocol has no explicit auth error — this
/// surfaces as a failure to decrypt or an integrity-hash mismatch.
/// </summary>
public class RainBirdAuthenticationException : RainBirdProtocolException
{
    public RainBirdAuthenticationException(string message) : base(message) { }
    public RainBirdAuthenticationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The controller replied with a NAK (SIP response <c>00</c>).</summary>
public class RainBirdNakException : RainBirdProtocolException
{
    public byte EchoedCommand { get; }
    public NakReason Reason { get; }

    public RainBirdNakException(byte echoedCommand, NakReason reason)
        : base($"Controller rejected command 0x{echoedCommand:X2}: {Describe(reason)}.")
    {
        EchoedCommand = echoedCommand;
        Reason = reason;
    }

    private static string Describe(NakReason reason) => reason switch
    {
        NakReason.CommandNotSupported => "command not supported by this model",
        NakReason.BadLength => "bad length",
        NakReason.IncompatibleData => "incompatible data",
        NakReason.ChecksumError => "checksum error",
        _ => "unknown reason",
    };
}

/// <summary>NAK codes from <c>RPCTunnelSIP.ErrorReason</c>.</summary>
public enum NakReason : byte
{
    Unknown = 0x00,
    CommandNotSupported = 0x01,
    BadLength = 0x02,
    IncompatibleData = 0x04,
    ChecksumError = 0x08,
}

/// <summary>The controller could not be reached, or stopped responding.</summary>
public class RainBirdConnectionException : RainBirdProtocolException
{
    public RainBirdConnectionException(string message) : base(message) { }
    public RainBirdConnectionException(string message, Exception inner) : base(message, inner) { }
}
