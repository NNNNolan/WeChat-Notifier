namespace WeChat_Notifier.Configuration;

/// <summary>名单身份只认规范化后的完整 EXE 路径，大小写不敏感。</summary>
internal static class ApplicationRules
{
    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            throw new InvalidDataException("请选择 EXE 的完整路径。");
        var full = Path.GetFullPath(path.Trim());
        if (!Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模式名单只支持 EXE 程序。");
        return full;
    }

    internal static void Validate(AppConfig config)
    {
        if (config.SilentApplications == null || config.PrivacyApplications == null)
            throw new InvalidDataException("模式名单不能为 null。");
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in config.SilentApplications.Concat(config.PrivacyApplications))
            if (!all.Add(Normalize(path))) throw new InvalidDataException("相同 EXE 只能出现在一个模式名单中，不能重复。");
    }

    internal static bool Contains(IEnumerable<string> list, string path)
    {
        var normalized = Normalize(path);
        return list.Any(item => Normalize(item).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }
}
