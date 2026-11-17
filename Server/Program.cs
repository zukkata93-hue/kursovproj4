using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Shared;

var config = JsonSerializer.Deserialize<Settings>(File.ReadAllText("appsettings.json"), Json.Options) ?? throw new Exception("Invalid configuration");
var db = new Database(config.ConnectionString);
await db.Initialize(config);
var certificate = new X509Certificate2(config.CertificatePath, config.CertificatePassword);
var listener = new TcpListener(IPAddress.Any, config.Port); listener.Start();
Console.WriteLine($"TCP/TLS server: {config.Port}");
while (true) { var client = await listener.AcceptTcpClientAsync(); _ = Task.Run(() => Serve(client)); }
async Task Serve(TcpClient client) {
 using (client) using (var tls = new SslStream(client.GetStream())) {
  try {
   await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
   using var reader = new StreamReader(tls, Encoding.UTF8, false, 4096, true); using var writer = new StreamWriter(tls, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
   string? role = null; int? userId = null;
   while (await reader.ReadLineAsync() is { } line) {
    Response result;
    try {
     if (line.Length > 100000) throw new Exception("Слишком большой запрос");
     var request = JsonSerializer.Deserialize<Request>(line, Json.Options) ?? throw new Exception("Неверный запрос");
     if (request.Action == "login") { var u = await db.Login(request.Data); role = u.Role; userId = u.Id; result = new() { Ok = true, Data = role }; }
     else { if (role == null) throw new Exception("Требуется авторизация"); result = new() { Ok = true, Data = await db.Execute(request.Action, request.Data, role, userId!.Value) }; }
    } catch (Exception ex) { result = new() { Ok = false, Message = ex is SqlException ? "Ошибка базы данных: проверьте данные и связи записей." : ex.Message }; }
    await writer.WriteLineAsync(JsonSerializer.Serialize(result));
   }
  } catch (Exception ex) { Console.WriteLine("Клиент отключён: " + ex.Message); }
 }
}
class Settings { public int Port { get; set; } public string ConnectionString { get; set; } = ""; public string CertificatePath { get; set; } = ""; public string CertificatePassword { get; set; } = ""; public string InitialAdminLogin { get; set; } = ""; public string InitialAdminPassword { get; set; } = ""; }
static class Passwords {
 public static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32); return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash); }
 public static bool Verify(string password, string stored) { var parts = stored.Split(':'); if (parts.Length != 2) return false; var expected = Convert.FromBase64String(parts[1]); var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[0]), 210000, HashAlgorithmName.SHA256, expected.Length); return CryptographicOperations.FixedTimeEquals(actual, expected); }
}
class Database {
 readonly string connection; public Database(string connection) => this.connection = connection;
 async Task<SqlConnection> Open() { var c = new SqlConnection(connection); await c.OpenAsync(); return c; }
 public async Task Initialize(Settings s) { using var c = await Open(); using (var cmd = new SqlCommand(await File.ReadAllTextAsync("schema.sql"), c)) await cmd.ExecuteNonQueryAsync(); using var count = new SqlCommand("SELECT COUNT(*) FROM Users", c); if ((int)(await count.ExecuteScalarAsync())! == 0) { if (s.InitialAdminPassword.Length < 8) throw new Exception("Initial admin password must contain at least 8 characters"); using var cmd = new SqlCommand("INSERT INTO Users(Login,PasswordHash,Role) VALUES(@login,@hash,'Admin')", c); cmd.Parameters.AddWithValue("@login", s.InitialAdminLogin); cmd.Parameters.AddWithValue("@hash", Passwords.Hash(s.InitialAdminPassword)); await cmd.ExecuteNonQueryAsync(); Console.WriteLine("Создан администратор. Смените начальный пароль и удалите его из конфигурации."); } }
 static string Str(JsonElement d, string name, bool required = true) { var v = d.TryGetProperty(name, out var x) ? x.ToString().Trim() : ""; if (required && v.Length == 0) throw new Exception($"Заполните поле {name}"); return v; }
 static int Id(JsonElement d, string n = "Id") { if (!int.TryParse(Str(d,n), out int id) || id <= 0) throw new Exception($"Неверный {n}"); return id; }
 static DateTime Date(JsonElement d, string n) { if (!DateTime.TryParse(Str(d,n), out var v)) throw new Exception($"Неверная дата {n}"); return DateTime.SpecifyKind(v, DateTimeKind.Unspecified); }
 static void Admin(string role) { if (role != "Admin") throw new Exception("Доступно только администратору"); }
 static async Task<int> Run(SqlConnection c, string sql, params (string, object?)[] args) { using var cmd = new SqlCommand(sql,c); foreach(var (k,v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value); return await cmd.ExecuteNonQueryAsync(); }
 static async Task<List<Dictionary<string, object?>>> Query(SqlConnection c, string sql, params (string, object?)[] args) { using var cmd = new SqlCommand(sql,c); foreach(var (k,v) in args) cmd.Parameters.AddWithValue(k,v ?? DBNull.Value); using var r = await cmd.ExecuteReaderAsync(); var rows = new List<Dictionary<string,object?>>(); while(await r.ReadAsync()) { var row = new Dictionary<string,object?>(); for(int i=0;i<r.FieldCount;i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i); rows.Add(row); } return rows; }
 public async Task<(int Id,string Role)> Login(JsonElement d) { using var c = await Open(); var rows = await Query(c,"SELECT Id,Role,PasswordHash FROM Users WHERE Login=@login",("@login",Str(d,"Login"))); if (rows.Count == 0 || !Passwords.Verify(Str(d,"Password"), (string)rows[0]["PasswordHash"]!)) throw new Exception("Неверный логин или пароль"); return ((int)rows[0]["Id"]!, (string)rows[0]["Role"]!); }
 public async Task<object?> Execute(string action, JsonElement d, string role, int userId) { using var c = await Open();
 switch(action) {
 case "listEquipment": return await Query(c,"SELECT e.Id,e.Name,e.Model, COALESCE(a.Status,'NoEvent') AS CurrentStatus FROM Equipment e OUTER APPLY (SELECT TOP 1 Status FROM Events WHERE EquipmentId=e.Id AND EndedAt IS NULL) a ORDER BY e.Id");
 case "listEmployees": return await Query(c,"SELECT Id,Name,Position FROM Employees ORDER BY Id");
 case "listUsers": Admin(role); return await Query(c,"SELECT Id,Login,Role FROM Users ORDER BY Id");
 case "listOrders": return await Query(c,"SELECT Id,Number,EquipmentId,EmployeeId,Description,Status FROM Orders ORDER BY Id DESC");
 case "listEvents": return await Query(c,"SELECT Id,EquipmentId,OrderId,Status,Reason,StartedAt,EndedAt FROM Events ORDER BY StartedAt DESC");
 case "saveEquipment": Admin(role); return await Save(c,d,"Equipment",new[]{"Name","Model"});
 case "saveEmployee": Admin(role); return await Save(c,d,"Employees",new[]{"Name","Position"});
 case "saveOrder": {
  var status = Str(d,"Status"); if (!new[]{"New","InProgress","Done"}.Contains(status)) throw new Exception("Статус заказа: New, InProgress или Done");
  int? employee = string.IsNullOrWhiteSpace(Str(d,"EmployeeId",false)) ? null : Id(d,"EmployeeId");
  var args = new (string,object?)[]{("@number",Str(d,"Number")),("@eq",Id(d,"EquipmentId")),("@employee",employee),("@desc",Str(d,"Description")),("@status",status), ("@id", OptionalId(d))};
  return await Run(c,OptionalId(d)==null ? "INSERT Orders(Number,EquipmentId,EmployeeId,Description,Status) VALUES(@number,@eq,@employee,@desc,@status)" : "UPDATE Orders SET Number=@number,EquipmentId=@eq,EmployeeId=@employee,Description=@desc,Status=@status WHERE Id=@id",args);
 }
 case "saveUser": {
  Admin(role); var login=Str(d,"Login"); var r=Str(d,"Role"); if (r != "Admin" && r != "Dispatcher") throw new Exception("Роль: Admin или Dispatcher"); var id=OptionalId(d); var password=Str(d,"Password",false);
  if (id == userId && r != "Admin") throw new Exception("Нельзя изменить собственную роль администратора");
  if ((id == null || password.Length > 0) && password.Length < 8) throw new Exception("Пароль должен содержать не менее 8 символов");
  if (id == null) return await Run(c,"INSERT Users(Login,Role,PasswordHash) VALUES(@login,@role,@hash)",("@login",login),("@role",r),("@hash",Passwords.Hash(password)));
  return await Run(c, password.Length == 0 ? "UPDATE Users SET Login=@login,Role=@role WHERE Id=@id" : "UPDATE Users SET Login=@login,Role=@role,PasswordHash=@hash WHERE Id=@id",("@login",login),("@role",r),("@id",id),("@hash",password.Length == 0 ? "" : Passwords.Hash(password)));
 }
 case "delete": { var entity=Str(d,"Entity"); var id=Id(d); if (entity is "Equipment" or "Employees" or "Users") Admin(role); if (!new[]{"Equipment","Employees","Users","Orders"}.Contains(entity)) throw new Exception("Неверный тип записи"); if (entity == "Users" && id == userId) throw new Exception("Нельзя удалить свою учетную запись"); return await Run(c,$"DELETE FROM {entity} WHERE Id=@id",("@id",id)); }
 case "startEvent": {
  int eq=Id(d,"EquipmentId"); var status=Str(d,"Status"); if (!new[]{"Work","Idle","Setup"}.Contains(status)) throw new Exception("Статус: Work, Idle или Setup"); var reason=Str(d,"Reason",false); if (status=="Idle" && reason.Length==0) throw new Exception("Укажите причину простоя"); int? order=string.IsNullOrWhiteSpace(Str(d,"OrderId",false)) ? null : Id(d,"OrderId");
  // One transaction serializes transitions for the same equipment and prevents overlapping active events.
  using var tx = (SqlTransaction)await c.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
  try { using var cmd = new SqlCommand("SELECT Id FROM Equipment WITH (UPDLOCK,HOLDLOCK) WHERE Id=@eq",c,tx); cmd.Parameters.AddWithValue("@eq",eq); if (await cmd.ExecuteScalarAsync() == null) throw new Exception("Оборудование не найдено");
   if(order != null) { using var check = new SqlCommand("SELECT COUNT(*) FROM Orders WHERE Id=@order AND EquipmentId=@eq",c,tx); check.Parameters.AddWithValue("@order",order); check.Parameters.AddWithValue("@eq",eq); if ((int)(await check.ExecuteScalarAsync())! == 0) throw new Exception("Заказ не назначен этому оборудованию"); }
   using var change = new SqlCommand("DECLARE @now datetime2=SYSDATETIME(); UPDATE Events SET EndedAt=CASE WHEN StartedAt >= @now THEN DATEADD(millisecond,1,StartedAt) ELSE @now END WHERE EquipmentId=@eq AND EndedAt IS NULL; INSERT Events(EquipmentId,OrderId,Status,Reason,StartedAt) VALUES(@eq,@order,@status,@reason,@now)",c,tx); change.Parameters.AddWithValue("@eq",eq); change.Parameters.AddWithValue("@order",(object?)order ?? DBNull.Value); change.Parameters.AddWithValue("@status",status); change.Parameters.AddWithValue("@reason",reason); await change.ExecuteNonQueryAsync(); await tx.CommitAsync(); return "Статус изменён";
  } catch { await tx.RollbackAsync(); throw; }
 }
 case "endEvent": return await Run(c,"UPDATE Events SET EndedAt=CASE WHEN StartedAt >= SYSDATETIME() THEN DATEADD(millisecond,1,StartedAt) ELSE SYSDATETIME() END WHERE EquipmentId=@eq AND EndedAt IS NULL",("@eq",Id(d,"EquipmentId")));
 case "backup": { Admin(role); var path=Str(d,"Path");
  // The path is interpreted by SQL Server, not by the client or the application server.
  using var nameCmd = new SqlCommand("SELECT DB_NAME()", c); var database = (string)(await nameCmd.ExecuteScalarAsync())!;
  var safeName = "[" + database.Replace("]", "]]") + "]";
  using var backup = new SqlCommand($"BACKUP DATABASE {safeName} TO DISK=@path WITH COPY_ONLY, CHECKSUM", c); backup.CommandTimeout=0; backup.Parameters.AddWithValue("@path",path); await backup.ExecuteNonQueryAsync(); return "Резервная копия создана на сервере SQL Server";
 }
 case "analytics": { var from=Date(d,"From"); var to=Date(d,"To"); if(to<=from) throw new Exception("Конец периода должен быть позже начала");
  return await Query(c,@"SELECT e.Id, e.Name, CAST(COALESCE(SUM(CASE WHEN v.Status='Work' THEN v.Minutes ELSE 0 END),0) AS decimal(18,2)) AS WorkMinutes, CAST(COALESCE(SUM(CASE WHEN v.Status='Idle' THEN v.Minutes ELSE 0 END),0) AS decimal(18,2)) AS IdleMinutes, CAST(COALESCE(SUM(CASE WHEN v.Status='Setup' THEN v.Minutes ELSE 0 END),0) AS decimal(18,2)) AS SetupMinutes, CAST(100.0*COALESCE(SUM(CASE WHEN v.Status='Work' THEN v.Minutes ELSE 0 END),0)/NULLIF(DATEDIFF_BIG(second,@from,@to)/60.0,0) AS decimal(8,2)) AS LoadPercent FROM Equipment e OUTER APPLY (SELECT Status, DATEDIFF_BIG(second,CASE WHEN StartedAt<@from THEN @from ELSE StartedAt END, CASE WHEN COALESCE(EndedAt,SYSDATETIME())>@to THEN @to ELSE COALESCE(EndedAt,SYSDATETIME()) END)/60.0 AS Minutes FROM Events WHERE EquipmentId=e.Id AND StartedAt<@to AND COALESCE(EndedAt,SYSDATETIME())>@from) v GROUP BY e.Id,e.Name ORDER BY e.Id",("@from",from),("@to",to)); }
 default: throw new Exception("Неизвестная команда");
 } }
 static int? OptionalId(JsonElement d) => string.IsNullOrWhiteSpace(Str(d,"Id",false)) ? null : Id(d);
 static async Task<int> Save(SqlConnection c, JsonElement d,string table,string[] fields) { var id=OptionalId(d); var args=fields.Select(f => ("@"+f,(object?)Str(d,f))).Append(("@id",(object?)id)).ToArray(); var sql=id==null ? $"INSERT {table}({string.Join(',',fields)}) VALUES({string.Join(',',fields.Select(f=>"@"+f))})" : $"UPDATE {table} SET {string.Join(',',fields.Select(f=>f+"=@"+f))} WHERE Id=@id"; return await Run(c,sql,args); }
}
