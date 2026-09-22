using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenCodeSpend.Data;

public sealed class ZenStore(Db db)
{
    private readonly Db _db = db;

    private static string Owner(string? uid) => string.IsNullOrWhiteSpace(uid) ? Db.LocalUser : uid;

    public Task ReplaceAsync(string period, string scope, List<Dictionary<string, string>> rows, string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var tx = cn.BeginTransaction();

        using (var del = cn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "delete from zen_usage where user_id=@1 and period=@2 and scope=@3";
            del.Parameters.AddWithValue("@1", Owner(uid));
            del.Parameters.AddWithValue("@2", period);
            del.Parameters.AddWithValue("@3", scope);
            del.ExecuteNonQuery();
        }
        using (var ins = cn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "insert into zen_usage(user_id,period,scope,row) values(@1,@2,@3,@4)";
            ins.Parameters.Add(new SqliteParameter { ParameterName = "@1" });
            ins.Parameters.Add(new SqliteParameter { ParameterName = "@2" });
            ins.Parameters.Add(new SqliteParameter { ParameterName = "@3" });
            ins.Parameters.Add(new SqliteParameter { ParameterName = "@4" });
            foreach (var r in rows)
            {
                ins.Parameters[0].Value = Owner(uid);
                ins.Parameters[1].Value = period;
                ins.Parameters[2].Value = scope;
                ins.Parameters[3].Value = JsonSerializer.Serialize(r);
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, object>> GetAsync(string? uid = null, CancellationToken ct = default)
    {
        var periods = new Dictionary<string, object>();
        var columns = new List<string>();
        using var cn = _db.Open();
        foreach (var period in ZenClient.Ranges)
        {
            var rows = new List<Dictionary<string, string>>();
            string? fetched = null;
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "select row, fetched_at from zen_usage where user_id=@2 and period=@1 order by fetched_at desc limit 5000";
                cmd.Parameters.AddWithValue("@1", period);
                cmd.Parameters.AddWithValue("@2", Owner(uid));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add(JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(0))!);
                    fetched ??= r.IsDBNull(1) ? null : r.GetString(1);
                }
            }
            foreach (var k in rows.SelectMany(x => x.Keys))
                if (!columns.Contains(k)) columns.Add(k);
            periods[period] = new { fetchedAt = fetched, count = rows.Count, rows = rows.Take(200) };
        }
        return Task.FromResult(new Dictionary<string, object> { ["columns"] = columns, ["periods"] = periods });
    }
}
