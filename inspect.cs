using Microsoft.Data.Sqlite;
var path = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\VerseOps\inventory.db");
Console.WriteLine($"DB: {path}");
using var c = new SqliteConnection($"Data Source={path}");
c.Open();
foreach (var sql in new[] {
    "SELECT 'envs    '||COUNT(*) FROM gov_environment",
    "SELECT 'caps    '||COUNT(*) FROM gov_capacity",
    "SELECT 'assets  '||COUNT(*) FROM gov_asset",
    "SELECT 'envs_w  '||COUNT(DISTINCT env_id) FROM gov_asset WHERE env_id IS NOT NULL"
}) { var cmd = c.CreateCommand(); cmd.CommandText = sql; Console.WriteLine(cmd.ExecuteScalar()); }
Console.WriteLine("---- by asset_type ----");
{ var cmd = c.CreateCommand(); cmd.CommandText="SELECT asset_type, COUNT(*) FROM gov_asset GROUP BY asset_type ORDER BY 2 DESC"; var r=cmd.ExecuteReader(); while(r.Read()) Console.WriteLine($"  {r.GetString(0),-20} {r.GetInt64(1)}"); }
Console.WriteLine("---- top 5 envs by asset count ----");
{ var cmd = c.CreateCommand(); cmd.CommandText="SELECT env_id, COUNT(*) FROM gov_asset WHERE env_id IS NOT NULL GROUP BY env_id ORDER BY 2 DESC LIMIT 5"; var r=cmd.ExecuteReader(); while(r.Read()) Console.WriteLine($"  {r.GetString(0)} {r.GetInt64(1)}"); }
Console.WriteLine("---- sample env_id from gov_environment vs gov_asset ----");
{ var cmd = c.CreateCommand(); cmd.CommandText="SELECT env_id FROM gov_environment LIMIT 3"; var r=cmd.ExecuteReader(); while(r.Read()) Console.WriteLine($"  ENV  {r.GetString(0)}"); }
{ var cmd = c.CreateCommand(); cmd.CommandText="SELECT DISTINCT env_id FROM gov_asset WHERE env_id IS NOT NULL LIMIT 3"; var r=cmd.ExecuteReader(); while(r.Read()) Console.WriteLine($"  ASSET {r.GetString(0)}"); }
