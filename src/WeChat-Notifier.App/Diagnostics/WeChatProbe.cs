using System.Diagnostics;
using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.WeChat.Database;

namespace WeChat_Notifier.Diagnostics;

/// <summary>Exercises the live database reader without starting notifications.</summary>
internal static class WeChatProbe
{
    public static int Run()
    {
        try
        {
            var options = ConfigStore.Load();
            using var self = Process.GetCurrentProcess();
            using var db = WeChatDatabase.Open(Console.Error.WriteLine);
            using var monitor = new DatabaseChangeMonitor(db.DatabasePaths);
            var sessions = db.ReadChanges()!.Sessions;
            var previous = sessions.ToDictionary(s => s.Username);
            var changes = 0;
            var initialQueries = db.SessionQueries;
            var events = 0;
            var periodic = 0;
            var cpu = self.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!stop.IsCancellationRequested)
            {
                var wake = monitor.WaitForCheck(stop.Token, options.PollIntervalMs);
                if (wake == null) break;
                if (wake.Value.Error != null) throw new IOException("文件监听失败", wake.Value.Error);
                if (wake.Value.Periodic) periodic++;
                else events++;
                WeChatDatabase.SessionRead? read;
                try { read = db.ReadChanges(); }
                catch (DatabaseBusyException)
                {
                    if (stop.Token.WaitHandle.WaitOne(DatabaseChangeMonitor.DebounceMs)) break;
                    monitor.RequestCheck();
                    continue;
                }
                if (read == null) continue;
                sessions = read.Sessions;
                changes += sessions.Count(s => !previous.TryGetValue(s.Username, out var old) || old != s);
                previous = sessions.ToDictionary(s => s.Username);
            }
            var withSummary = sessions.Count(s => !string.IsNullOrWhiteSpace(s.Summary));
            Console.WriteLine($"sessions={sessions.Count} withSummary={withSummary} " +
                $"withNickname={sessions.Count(s => s.Nickname != s.Username)} unread={sessions.Count(s => s.Unread > 0)} " +
                $"muted={sessions.Count(s => s.Muted)} official={sessions.Count(s => s.Official)} " +
                $"eventChecks={events} periodicChecks={periodic} sessionQueries={db.SessionQueries - initialQueries} " +
                $"contactQueries={db.ContactQueries} businessQueries={db.BusinessQueries} snapshotBuilds={db.SnapshotBuilds} " +
                $"changes={changes} elapsedMs={timer.ElapsedMilliseconds} " +
                $"cpuMs={(self.TotalProcessorTime - cpu).TotalMilliseconds:F1}");
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
