using Jukebox.Core.Domain;

namespace Jukebox.Core.Backup;

public static class RetentionPolicy
{
    public static IReadOnlyList<BackupRecord> Expired(IEnumerable<BackupRecord> all)
    {
        var drop = new List<BackupRecord>();
        foreach (var group in all.GroupBy(item => item.Kind))
        {
            var keep = group.Key switch
            {
                "daily" => 7,
                "weekly" => 4,
                "monthly" => 12,
                "manual" => 10,
                "pre-update" => 5,
                _ => 7
            };

            drop.AddRange(group.OrderByDescending(item => item.CreatedAt, StringComparer.Ordinal).Skip(keep));
        }

        return drop;
    }
}
