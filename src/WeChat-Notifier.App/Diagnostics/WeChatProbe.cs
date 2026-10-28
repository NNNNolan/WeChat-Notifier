using System.Diagnostics;
using WeChat_Notifier.Infrastructure.WeChat.Database;

namespace WeChat_Notifier.Diagnostics;

/// <summary>Exercises the live database reader without starting notifications.</summary>
internal static class WeChatProbe
{
    public static int Run()
    {
        try
        {
            var timer = Stopwatch.StartNew();
            using var db = WeChatDatabase.Open(Console.Error.WriteLine);
            var sessions = db.GetSessions();
            var previous = sessions.ToDictionary(s => s.Username);
            var changes = 0;
            for (var i = 0; i < 20; i++)
            {
                Thread.Sleep(600);
                sessions = db.GetSessions();
                changes += sessions.Count(s => !previous.TryGetValue(s.Username, out var old) || old != s);
                previous = sessions.ToDictionary(s => s.Username);
            }
            var withSummary = sessions.Count(s => !string.IsNullOrWhiteSpace(s.Summary));
            Console.WriteLine($"sessions={sessions.Count} withSummary={withSummary} " +
                $"withNickname={sessions.Count(s => s.Nickname != s.Username)} unread={sessions.Count(s => s.Unread > 0)} " +
                $"muted={sessions.Count(s => s.Muted)} official={sessions.Count(s => s.Official)} " +
                $"polls=21 changes={changes} elapsedMs={timer.ElapsedMilliseconds}");
            if (withSummary == 0)
                throw new InvalidOperationException("未读取到消息摘要");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
