namespace WeChat_Notifier.Domain;

/// <summary>会话、联系人与企业信息合并后的数据库快照。</summary>
public sealed record WeChatSession(
    string Username, string Nickname, string Summary, long LastTimestamp,
    string LastSender, int Unread, bool Muted, bool Official)
{
    public string CompanyName { get; init; } = "";
    public string DisplayName => string.IsNullOrWhiteSpace(CompanyName) ? Nickname : $"{CompanyName}- {Nickname}";
    public bool IsGroup => Username.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)
        || Username.EndsWith("@qy_g", StringComparison.OrdinalIgnoreCase);
}
