using System.Data;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Shared;
namespace Client;
public partial class MainWindow : Window {
 TcpClient? client; SslStream? tls; StreamReader? reader; StreamWriter? writer; string role="";
 readonly SemaphoreSlim gate = new(1,1);
 public MainWindow() { InitializeComponent(); Closed += (_,_) => { tls?.Dispose(); client?.Dispose(); }; }
 async Task<JsonElement> Call(string action, object data) {
  await gate.WaitAsync(); try {
   if (writer == null || reader == null) throw new Exception("Нет соединения с сервером");
   await writer.WriteLineAsync(JsonSerializer.Serialize(new { Action=action, Data=data }));
   var line=await reader.ReadLineAsync() ?? throw new Exception("Соединение закрыто сервером");
   using var doc=JsonDocument.Parse(line); var root=doc.RootElement;
   if (!root.GetProperty("Ok").GetBoolean()) throw new Exception(root.GetProperty("Message").GetString());
   return root.TryGetProperty("Data",out var result) ? result.Clone() : default;
  } finally { gate.Release(); }
 }
 async void LoginClick(object sender, RoutedEventArgs e) {
  Tabs.IsEnabled=false; Tabs.Items.Clear(); tls?.Dispose(); client?.Dispose(); reader=null; writer=null; ConnectionStatus.Text="Нет подключения";
  try {
   var settings=JsonSerializer.Deserialize<ClientSettings>(await File.ReadAllTextAsync("clientsettings.json"),Json.Options) ?? throw new Exception("Неверная конфигурация");
   var expected=settings.CertificateSha256.Replace(":","").Replace(" ","").ToUpperInvariant(); if(expected.Length!=64 || !expected.All(Uri.IsHexDigit)) throw new Exception("Укажите SHA-256 отпечаток сертификата сервера в clientsettings.json");
   client=new TcpClient(); await client.ConnectAsync(settings.Host,settings.Port);
   tls=new SslStream(client.GetStream(),false,(_,cert,_,_) => cert != null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()),Convert.FromHexString(expected)));
   await tls.AuthenticateAsClientAsync(settings.Host);
   reader=new StreamReader(tls,Encoding.UTF8,false,4096,true); writer=new StreamWriter(tls,new UTF8Encoding(false),4096,true){AutoFlush=true};
   role=(await Call("login",new { Login=Login.Text, Password=Password.Password })).GetString() ?? ""; Password.Clear(); ConnectionStatus.Text=role=="Admin" ? "Администратор" : "Диспетчер";
   BuildTabs(); Tabs.IsEnabled=true;
  } catch(Exception ex) { MessageBox.Show(ex.Message,"Ошибка входа"); tls?.Dispose(); client?.Dispose(); reader=null; writer=null; }
 }
 static Button Button(string text) => new() { Content=text, Padding=new Thickness(10,5,10,5), Margin=new Thickness(0,0,6,6) };
 static StackPanel Row() => new() { Orientation=Orientation.Horizontal };
 static TextBox Field(Panel panel,string label,string value="", double width=130) { panel.Children.Add(new TextBlock { Text=label, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(0,0,5,6) }); var box=new TextBox { Text=value, Width=width, Margin=new Thickness(0,0,10,6), VerticalContentAlignment=VerticalAlignment.Center }; panel.Children.Add(box); return box; }
 void BuildTabs() {
  AddCrud("Оборудование","listEquipment","saveEquipment","Equipment",new[]{("Name","Название"),("Model","Модель")},role=="Admin");
  AddCrud("Сотрудники","listEmployees","saveEmployee","Employees",new[]{("Name","ФИО"),("Position","Должность")},role=="Admin");
  AddCrud("Заказы","listOrders","saveOrder","Orders",new[]{("Number","Номер"),("EquipmentId","ID оборудования"),("EmployeeId","ID сотрудника (необ.)"),("Description","Описание"),("Status","New/InProgress/Done")},true);
  if(role=="Admin") AddCrud("Пользователи","listUsers","saveUser","Users",new[]{("Login","Логин"),("Role","Admin/Dispatcher"),("Password","Новый пароль")},true);
  AddEvents(); AddAnalytics(); if(role=="Admin") AddBackup();
 }
 static DataTable Table(JsonElement data) { var t=new DataTable(); if(data.ValueKind != JsonValueKind.Array || data.GetArrayLength()==0) return t; foreach(var p in data[0].EnumerateObject()) t.Columns.Add(p.Name); foreach(var item in data.EnumerateArray()) { var row=t.NewRow(); foreach(var p in item.EnumerateObject()) row[p.Name]=p.Value.ValueKind==JsonValueKind.Null ? "" : p.Value.ToString(); t.Rows.Add(row); } return t; }
 async Task Refresh(DataGrid grid,string action) { grid.ItemsSource=Table(await Call(action,new {})).DefaultView; }
 static DataGrid Grid() => new() { AutoGenerateColumns=true, IsReadOnly=true, CanUserAddRows=false, SelectionMode=DataGridSelectionMode.Single, Margin=new Thickness(0,8,0,0) };
 void AddCrud(string title,string list,string save,string entity,(string key,string label)[] fields,bool editable) {
  var dock=new DockPanel(); var top=new StackPanel(); DockPanel.SetDock(top,Dock.Top); dock.Children.Add(top); var buttons=Row(); top.Children.Add(buttons); var refresh=Button("Обновить"); buttons.Children.Add(refresh); var grid=Grid(); dock.Children.Add(grid);
  var form=new WrapPanel(); top.Children.Add(form); var boxes=new Dictionary<string,TextBox>(); foreach(var (key,label) in fields) boxes[key]=Field(form,label);
  string selectedId=""; grid.SelectionChanged += (_,_) => { if(grid.SelectedItem is DataRowView row) { selectedId=row["Id"].ToString() ?? ""; foreach(var (key,box) in boxes) box.Text=row.Row.Table.Columns.Contains(key) ? row[key].ToString() ?? "" : ""; } };
  refresh.Click += async (_,_) => { try { await Refresh(grid,list); } catch(Exception ex) { ShowError(ex); } };
  if(editable) {
   var clear=Button("Новая запись"); var submit=Button("Сохранить"); var delete=Button("Удалить выбранную"); buttons.Children.Add(clear); buttons.Children.Add(submit); buttons.Children.Add(delete);
   clear.Click += (_,_) => { selectedId=""; grid.UnselectAll(); foreach(var b in boxes.Values) b.Clear(); };
   submit.Click += async (_,_) => { try { var data=boxes.ToDictionary(x=>x.Key,x=>x.Value.Text); data["Id"]=selectedId; await Call(save,data); selectedId=""; foreach(var b in boxes.Values) b.Clear(); await Refresh(grid,list); } catch(Exception ex) { ShowError(ex); } };
   delete.Click += async (_,_) => { if(string.IsNullOrEmpty(selectedId)) { MessageBox.Show("Выберите запись"); return; } if(MessageBox.Show("Удалить запись?", "Подтверждение", MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return; try { await Call("delete",new { Entity=entity,Id=selectedId }); selectedId=""; await Refresh(grid,list); } catch(Exception ex) { ShowError(ex); } };
  } else form.IsEnabled=false;
  Tabs.Items.Add(new TabItem { Header=title,Content=dock });
 }
 void AddEvents() {
  var dock=new DockPanel(); var top=new StackPanel(); DockPanel.SetDock(top,Dock.Top); dock.Children.Add(top); var form=new WrapPanel(); top.Children.Add(form);
  var eq=Field(form,"ID оборудования"); var order=Field(form,"ID заказа (необ.)"); var status=Field(form,"Work / Idle / Setup","Work"); var reason=Field(form,"Причина простоя", "",180);
  var buttons=Row(); top.Children.Add(buttons); var start=Button("Начать / сменить статус"); var end=Button("Завершить событие"); var refresh=Button("Обновить"); buttons.Children.Add(start);buttons.Children.Add(end);buttons.Children.Add(refresh); var grid=Grid(); dock.Children.Add(grid);
  start.Click += async (_,_) => { try { await Call("startEvent",new { EquipmentId=eq.Text,OrderId=order.Text,Status=status.Text,Reason=reason.Text }); await Refresh(grid,"listEvents"); } catch(Exception ex) { ShowError(ex); } };
  end.Click += async (_,_) => { try { var count=await Call("endEvent",new { EquipmentId=eq.Text }); if(count.GetInt32()==0) MessageBox.Show("Активное событие не найдено"); await Refresh(grid,"listEvents"); } catch(Exception ex) { ShowError(ex); } };
  refresh.Click += async (_,_) => { try { await Refresh(grid,"listEvents"); } catch(Exception ex) { ShowError(ex); } };
  Tabs.Items.Add(new TabItem { Header="События",Content=dock });
 }
 void AddAnalytics() {
  var dock=new DockPanel(); var top=new StackPanel(); DockPanel.SetDock(top,Dock.Top); dock.Children.Add(top); var row=new WrapPanel(); top.Children.Add(row);
  var from=Field(row,"С",DateTime.Today.ToString("yyyy-MM-dd HH:mm"),150); var to=Field(row,"До",DateTime.Now.ToString("yyyy-MM-dd HH:mm"),150);
  var shift=Button("8 часов"); var day=Button("Сутки"); var week=Button("Неделя"); var calculate=Button("Рассчитать"); var save=Button("Сохранить отчёт"); foreach(var b in new[]{shift,day,week,calculate,save}) row.Children.Add(b);
  void Period(int hours) { var now=DateTime.Now; from.Text=now.AddHours(-hours).ToString("yyyy-MM-dd HH:mm"); to.Text=now.ToString("yyyy-MM-dd HH:mm"); }
  shift.Click+=(_,_)=>Period(8); day.Click+=(_,_)=>Period(24); week.Click+=(_,_)=>Period(168);
  var content=new Grid(); content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition()); var grid=Grid(); content.Children.Add(grid); var chart=new StackPanel(); var scroll=new ScrollViewer { Content=chart,VerticalScrollBarVisibility=ScrollBarVisibility.Auto }; System.Windows.Controls.Grid.SetRow(scroll,1); content.Children.Add(scroll); dock.Children.Add(content);
  DataTable? report=null; string reportFrom="",reportTo="";
  calculate.Click+=async (_,_)=> { try { var calculatedFrom=from.Text; var calculatedTo=to.Text; var data=await Call("analytics",new { From=calculatedFrom,To=calculatedTo }); report=Table(data); reportFrom=calculatedFrom; reportTo=calculatedTo; grid.ItemsSource=report.DefaultView; chart.Children.Clear(); foreach(DataRow r in report.Rows) { var line=Row(); line.Children.Add(new TextBlock { Text=r["Name"]+"  "+r["LoadPercent"]+"%",Width=200 }); if(!double.TryParse(r["LoadPercent"].ToString(),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out var value)) value=0; line.Children.Add(new Border { Width=Math.Max(1,Math.Min(100,value)*4),Height=18,Background=Brushes.SteelBlue,HorizontalAlignment=HorizontalAlignment.Left }); chart.Children.Add(line); } } catch(Exception ex) { ShowError(ex); } };
  save.Click+=(_,_)=> { if(report==null) { MessageBox.Show("Сначала рассчитайте отчёт"); return; } var dialog=new SaveFileDialog { Filter="Текстовый файл|*.txt",FileName="report.txt" }; if(dialog.ShowDialog()!=true) return; try { var s=new StringBuilder(); s.AppendLine($"Загрузка оборудования: {reportFrom} — {reportTo}"); s.AppendLine(string.Join("\t",report.Columns.Cast<DataColumn>().Select(c=>c.ColumnName))); foreach(DataRow r in report.Rows) s.AppendLine(string.Join("\t",r.ItemArray)); File.WriteAllText(dialog.FileName,s.ToString(),Encoding.UTF8); } catch(Exception ex) { ShowError(ex); } };
  Tabs.Items.Add(new TabItem { Header="Аналитика",Content=dock });
 }
 void AddBackup() {
  var panel=new StackPanel { Margin=new Thickness(10) }; panel.Children.Add(new TextBlock { Text="Путь к файлу на компьютере SQL Server (учётной записи SQL Server нужна запись в папку):", TextWrapping=TextWrapping.Wrap });
  var path=new TextBox { Margin=new Thickness(0,8,0,8), Text=@"C:\Backups\FactoryLoad.bak" }; panel.Children.Add(path); var button=Button("Создать резервную копию"); button.HorizontalAlignment=HorizontalAlignment.Left; panel.Children.Add(button);
  button.Click += async (_,_) => { try { var result=await Call("backup",new { Path=path.Text }); MessageBox.Show(result.GetString() ?? "Готово"); } catch(Exception ex) { ShowError(ex); } };
  Tabs.Items.Add(new TabItem { Header="Резервное копирование", Content=panel });
 }
 static void ShowError(Exception ex) => MessageBox.Show(ex.Message,"Ошибка");
}
class ClientSettings { public string Host { get; set; }="localhost"; public int Port { get; set; }=5050; public string CertificateSha256 { get; set; }=""; }
