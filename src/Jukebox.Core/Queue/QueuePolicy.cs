namespace Jukebox.Core.Queue;

public enum QueueBlock
{
    None,
    NotReady,
    Hidden,
    NowPlaying,
    AlreadyQueued,
    Cooldown,
    Full
}

public static class QueuePolicy
{
    public const int MaxQueueLength = 100;

    public static QueueBlock Evaluate(bool ready, bool hidden, bool catalog, bool nowPlaying, bool alreadyQueued, bool playedRecently, bool queueFull, bool humanSelection)
    {
        if (!ready || !catalog)
            return QueueBlock.NotReady;
        if (hidden)
            return QueueBlock.Hidden;
        if (nowPlaying)
            return QueueBlock.NowPlaying;
        if (alreadyQueued)
            return QueueBlock.AlreadyQueued;
        if (humanSelection && playedRecently)
            return QueueBlock.Cooldown;
        if (queueFull)
            return QueueBlock.Full;
        return QueueBlock.None;
    }

    public static string? Message(QueueBlock block) => block switch
    {
        QueueBlock.None => null,
        QueueBlock.NotReady => "This film is not available.",
        QueueBlock.Hidden => "This film is not available.",
        QueueBlock.NowPlaying => "This is playing now.",
        QueueBlock.AlreadyQueued => "This is already in the queue.",
        QueueBlock.Cooldown => "This was played recently. You can choose it again later.",
        QueueBlock.Full => "The queue is full. Wait for a film to finish.",
        _ => "This film is not available."
    };
}
