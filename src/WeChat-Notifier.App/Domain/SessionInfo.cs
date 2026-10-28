namespace WeChat_Notifier.Domain;

/// <summary>数据库会话摘要转换后的通知内容。</summary>
public sealed class SessionInfo
{
    /// <summary>微信数据库中的会话 ID。</summary>
    public string Key { get; init; } = "";

    /// <summary>Conversation title as shown by WeChat.</summary>
    public string Title { get; init; } = "";

    /// <summary>Last-message preview, may span several lines.</summary>
    public string Preview { get; init; } = "";

    /// <summary>Timestamp label exactly as WeChat renders it (17:28, 09/01, 昨天 ...).</summary>
    public string Time { get; init; } = "";

    public bool Muted { get; init; }

    /// <summary>数据库中的未读数。</summary>
    public int Unread { get; init; }

    /// <summary>本次数据库快照中增加的消息数，不包含启动时已有的未读。</summary>
    public int NewMessageCount { get; init; } = 1;

    public override string ToString() => $"{Title} | {Time} | {Preview.Replace('\n', ' ')}";
}
