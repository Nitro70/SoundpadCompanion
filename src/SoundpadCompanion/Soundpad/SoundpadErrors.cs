namespace SoundpadCompanion.Soundpad;

/// <summary>Anything that went wrong talking to Soundpad.</summary>
public class SoundpadException : Exception
{
    public SoundpadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Soundpad could not be reached: not running, or it stopped answering.</summary>
public sealed class SoundpadOfflineException : SoundpadException
{
    public SoundpadOfflineException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The +30 dB mic boost can't be done; the message says why, in words for the overlay.</summary>
public sealed class MicBoostException : SoundpadException
{
    public MicBoostException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Soundpad answered, but with an error code (R-400, R-404, R-500...).</summary>
public sealed class SoundpadCommandException : SoundpadException
{
    public string Command { get; }
    public string Reply { get; }

    public SoundpadCommandException(string command, string reply)
        : base($"Soundpad answered {(reply.Length == 0 ? "nothing" : reply)} to {command}")
    {
        Command = command;
        Reply = reply;
    }
}
