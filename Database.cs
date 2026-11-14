using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SickoDistrict;

// Adaptador SQLite nativo do Windows. Sem pacotes NuGet externos.
public sealed class StoreException(string message) : Exception(message);
public sealed class StoreDb : IDisposable
{
    private IntPtr handle;
    private bool transaction;
    public Table<Customer> Customers { get; } = new();
    public Table<Product> Products { get; } = new();
    public Table<Variant> Variants { get; } = new();
    public Table<CartItem> CartItems { get; } = new();
    public Table<Order> Orders { get; } = new();
    private Table<OrderItem> Items { get; } = new();
    public StoreDb(IConfiguration config)
    {
        var configured = config.GetConnectionString("Store") ?? "Data Source=sicko.db";
        var file = configured.Split(';').First(x => x.Trim().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)).Split('=',2)[1].Trim();
        if (sqlite3_open(Utf8(file), out handle) != 0) throw new StoreException("Não foi possível abrir o banco SQLite.");
        sqlite3_busy_timeout(handle, 30000);
        Exec("PRAGMA foreign_keys=ON;");
        Exec("CREATE TABLE IF NOT EXISTS Customers(Id INTEGER PRIMARY KEY,Name TEXT NOT NULL,Email TEXT NOT NULL UNIQUE,PasswordHash TEXT NOT NULL,IsAdmin INTEGER NOT NULL);"+
             "CREATE TABLE IF NOT EXISTS Products(Id INTEGER PRIMARY KEY,Name TEXT NOT NULL,Category TEXT NOT NULL,Description TEXT NOT NULL,Price TEXT NOT NULL,Image TEXT NOT NULL,Active INTEGER NOT NULL);"+
             "CREATE TABLE IF NOT EXISTS Variants(Id INTEGER PRIMARY KEY,ProductId INTEGER NOT NULL REFERENCES Products(Id),Size TEXT NOT NULL,Stock INTEGER NOT NULL CHECK(Stock>=0),Version INTEGER NOT NULL,UNIQUE(ProductId,Size));"+
             "CREATE TABLE IF NOT EXISTS CartItems(Id INTEGER PRIMARY KEY,CustomerId INTEGER NOT NULL REFERENCES Customers(Id),VariantId INTEGER NOT NULL REFERENCES Variants(Id),Quantity INTEGER NOT NULL CHECK(Quantity>0 AND Quantity<=20),UNIQUE(CustomerId,VariantId));"+
             "CREATE TABLE IF NOT EXISTS Orders(Id INTEGER PRIMARY KEY,CustomerId INTEGER NOT NULL REFERENCES Customers(Id),CustomerName TEXT NOT NULL,CreatedAt TEXT NOT NULL,Address TEXT NOT NULL,Status TEXT NOT NULL,Payment TEXT NOT NULL,Total TEXT NOT NULL,Version INTEGER NOT NULL);"+
             "CREATE TABLE IF NOT EXISTS OrderItems(Id INTEGER PRIMARY KEY,OrderId INTEGER NOT NULL REFERENCES Orders(Id),VariantId INTEGER NOT NULL REFERENCES Variants(Id),ProductName TEXT NOT NULL,Size TEXT NOT NULL,Quantity INTEGER NOT NULL,UnitPrice TEXT NOT NULL);");
        // BEGIN IMMEDIATE serializa leitura + validação + gravação entre conexões.
        Exec("BEGIN IMMEDIATE"); transaction = true;
        try {
            Load("Customers", Customers); Load("Products", Products); Load("Variants", Variants);
            Load("CartItems", CartItems); Load("Orders", Orders); Load("OrderItems", Items);
            foreach (var p in Products) p.Variants = Variants.Where(v => v.ProductId == p.Id).ToList();
            foreach (var o in Orders) o.Items = Items.Where(i => i.OrderId == o.Id).ToList();
        } catch { Dispose(); throw; }
    }
    public Task<int> SaveChangesAsync()
    {
        if (!transaction) throw new StoreException("Esta unidade de trabalho já foi finalizada.");
        foreach (var p in Products) foreach (var v in p.Variants) { v.ProductId = p.Id; if (v.Id == 0) Variants.Add(v); }
        foreach (var o in Orders) foreach (var i in o.Items) { i.OrderId = o.Id; if (i.Id == 0) Items.Add(i); }
        try {
            // Conjunto pequeno para demonstração acadêmica: persiste um snapshot relacional atômico.
            Exec("DELETE FROM OrderItems;DELETE FROM CartItems;DELETE FROM Orders;DELETE FROM Variants;DELETE FROM Products;DELETE FROM Customers;");
            Write("Customers", Customers); Write("Products", Products); Write("Variants", Variants);
            Write("CartItems", CartItems); Write("Orders", Orders); Write("OrderItems", Items);
            Exec("COMMIT"); transaction = false; return Task.FromResult(1);
        } catch { if (transaction) { Exec("ROLLBACK"); transaction = false; } throw; }
    }
    private static PropertyInfo[] Fields<T>() => typeof(T).GetProperties().Where(p => !typeof(IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string)).ToArray();
    private void Load<T>(string table, Table<T> target) where T : class, new()
    {
        var fields=Fields<T>(); var sql=$"SELECT {string.Join(',',fields.Select(p=>'['+p.Name+']'))} FROM [{table}] ORDER BY Id";
        Check(sqlite3_prepare_v2(handle,Utf8(sql),-1,out var statement,IntPtr.Zero));
        try {
            int result;
            while ((result=sqlite3_step(statement))==100) {
                var item=new T();
                for(int k=0;k<fields.Length;k++) {
                    var p=fields[k]; var value=Marshal.PtrToStringUTF8(sqlite3_column_text(statement,k)) ?? "";
                    object converted=p.PropertyType==typeof(string)?value:p.PropertyType==typeof(bool)?value=="1":p.PropertyType==typeof(DateTime)?DateTime.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind):Convert.ChangeType(value,p.PropertyType,CultureInfo.InvariantCulture);
                    p.SetValue(item,converted);
                }
                target.Add(item);
            }
            if(result!=101) Check(result);
        } finally { sqlite3_finalize(statement); }
    }
    private void Write<T>(string table, IEnumerable<T> rows)
    {
        var fields=Fields<T>();
        var sql=$"INSERT INTO [{table}] ({string.Join(',',fields.Select(p=>'['+p.Name+']'))}) VALUES ({string.Join(',',fields.Select(_=>'?'))})";
        Check(sqlite3_prepare_v2(handle,Utf8(sql),-1,out var stmt,IntPtr.Zero));
        try { foreach(var row in rows) {
            for(int i=0;i<fields.Length;i++) {
                var value=fields[i].GetValue(row);
                var text=value switch { bool b => b?"1":"0",DateTime d=>d.ToString("O",CultureInfo.InvariantCulture),IFormattable f=>f.ToString(null,CultureInfo.InvariantCulture),_=>value?.ToString()??"" };
                var bytes=Encoding.UTF8.GetBytes(text); Check(sqlite3_bind_text(stmt,i+1,bytes,bytes.Length,new IntPtr(-1)));
            }
            if(sqlite3_step(stmt)!=101) throw new StoreException(Marshal.PtrToStringUTF8(sqlite3_errmsg(handle))??"Erro SQLite.");
            sqlite3_reset(stmt); sqlite3_clear_bindings(stmt);
        }} finally { sqlite3_finalize(stmt); }
    }
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s+'\0');
    private void Check(int code) { if(code!=0) throw new StoreException(Marshal.PtrToStringUTF8(sqlite3_errmsg(handle))??"Erro SQLite."); }
    private void Exec(string sql) { var code=sqlite3_exec(handle,Utf8(sql),IntPtr.Zero,IntPtr.Zero,out var error);if(code!=0){var message=Marshal.PtrToStringUTF8(error)??"Erro SQLite";sqlite3_free(error);throw new StoreException(message);} }
    public void Dispose() { if(handle==IntPtr.Zero)return;try { if(transaction) Exec("ROLLBACK"); } finally { sqlite3_close(handle);handle=IntPtr.Zero; } }
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_open(byte[] filename,out IntPtr db);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db,byte[] sql,IntPtr callback,IntPtr arg,out IntPtr error);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr ptr);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db,int ms);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db,byte[] sql,int n,out IntPtr statement,IntPtr tail);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement,int column);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement,int index,byte[] text,int length,IntPtr destructor);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_reset(IntPtr statement);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_clear_bindings(IntPtr statement);
}
public sealed class Table<T> : List<T> where T:class,new()
{
    private readonly PropertyInfo key=typeof(T).GetProperty("Id")!;
    private int nextId=1;
    public new void Add(T row) { var id=(int)key.GetValue(row)!;if(id==0)key.SetValue(row,nextId++);else nextId=Math.Max(nextId,id+1);base.Add(row); }
    public void RemoveRange(IEnumerable<T> rows) { foreach(var row in rows.ToList())Remove(row); }
    public ValueTask<T?> FindAsync(int id)=>ValueTask.FromResult(this.SingleOrDefault(x=>(int)key.GetValue(x)! == id));
}
public static class QueryExtensions
{
    public static IEnumerable<T> AsNoTracking<T>(this IEnumerable<T> rows)=>rows;
    public static IEnumerable<T> Include<T,V>(this IEnumerable<T> rows,Func<T,V> navigation)=>rows;
    public static Task<List<T>> ToListAsync<T>(this IEnumerable<T> rows)=>Task.FromResult(rows.ToList());
    public static Task<bool> AnyAsync<T>(this IEnumerable<T> rows)=>Task.FromResult(rows.Any());
    public static Task<bool> AnyAsync<T>(this IEnumerable<T> rows,Func<T,bool> predicate)=>Task.FromResult(rows.Any(predicate));
    public static Task<int> CountAsync<T>(this IEnumerable<T> rows)=>Task.FromResult(rows.Count());
    public static Task<int> CountAsync<T>(this IEnumerable<T> rows,Func<T,bool> predicate)=>Task.FromResult(rows.Count(predicate));
    public static Task<T?> SingleOrDefaultAsync<T>(this IEnumerable<T> rows,Func<T,bool> predicate)=>Task.FromResult(rows.SingleOrDefault(predicate));
}
