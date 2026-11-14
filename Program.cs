using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using SickoDistrict;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDataProtection().SetApplicationName("SickoDistrict").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".keys")));
builder.Services.AddScoped<StoreDb>();
builder.Services.AddAuthentication("Cookies").AddCookie(o => {
    o.Cookie.Name = "sicko.session"; o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddScoped<PasswordHasher<Customer>>();
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new() { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseAuthentication();
app.Use(async (ctx, next) => {
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    try {
        if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method))
            await ctx.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>().ValidateRequestAsync(ctx);
        await next();
    } catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) {
        ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Sessão expirada. Atualize a página." });
    } catch (StoreException) {
        ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "Os dados mudaram. Atualize e tente novamente." });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles(); app.UseAuthorization(); app.UseRateLimiter();
int UserId(ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
object PublicUser(Customer c) => new { c.Id, c.Name, c.Email, c.IsAdmin };
app.MapGet("/api/session", (HttpContext c, Microsoft.AspNetCore.Antiforgery.IAntiforgery a) =>
    Results.Ok(new { csrf = a.GetAndStoreTokens(c).RequestToken, authenticated = c.User.Identity?.IsAuthenticated ?? false }));
app.MapPost("/api/register", async (Credentials input, StoreDb db, PasswordHasher<Customer> hasher, HttpContext ctx) => {
    var email = (input.Email ?? "").Trim().ToLowerInvariant(); var name = (input.Name ?? "").Trim();
    if (name.Length < 2 || name.Length > 100 || email.Length > 200 || !System.Net.Mail.MailAddress.TryCreate(email, out _) || input.Password is null || input.Password.Length < 8 || input.Password.Length > 128)
        return Results.BadRequest(new { error = "Informe nome, e-mail válido e senha de 8 a 128 caracteres." });
    if (await db.Customers.AnyAsync(x => x.Email == email)) return Results.Conflict(new { error = "E-mail já cadastrado." });
    var c = new Customer { Name = name, Email = email }; c.PasswordHash = hasher.HashPassword(c, input.Password);
    db.Customers.Add(c);
    try { await db.SaveChangesAsync(); } catch (StoreException) { return Results.Conflict(new { error = "E-mail já cadastrado." }); }
    await SignIn(ctx, c); return Results.Ok(PublicUser(c));
}).RequireRateLimiting("auth");
app.MapPost("/api/login", async (Credentials input, StoreDb db, PasswordHasher<Customer> hasher, HttpContext ctx) => {
    var c = await db.Customers.SingleOrDefaultAsync(x => x.Email == (input.Email ?? "").Trim().ToLowerInvariant());
    if (c is null || input.Password is null || input.Password.Length > 128 || hasher.VerifyHashedPassword(c, c.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
        return Results.Json(new { error = "E-mail ou senha incorretos." }, statusCode: 401);
    await SignIn(ctx, c); return Results.Ok(PublicUser(c));
}).RequireRateLimiting("auth");
app.MapPost("/api/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); });
app.MapGet("/api/me", async (ClaimsPrincipal u, StoreDb db) => PublicUser((await db.Customers.FindAsync(UserId(u)))!)).RequireAuthorization();
app.MapGet("/api/products", async (StoreDb db) => await db.Products.AsNoTracking().Include(x => x.Variants).Where(x => x.Active).OrderBy(x => x.Id).ToListAsync());
app.MapGet("/api/cart", async (StoreDb db, ClaimsPrincipal u) => await Cart(db, UserId(u))).RequireAuthorization();
app.MapPut("/api/cart", async (CartInput input, StoreDb db, ClaimsPrincipal u) => {
    if (input.Quantity < 0 || input.Quantity > 20) return Results.BadRequest(new { error = "Quantidade deve estar entre 0 e 20." });
    var variant = await db.Variants.FindAsync(input.VariantId);
    var product = variant is null ? null : await db.Products.FindAsync(variant.ProductId);
    if (variant is null || product is null || !product.Active || input.Quantity > variant.Stock) return Results.BadRequest(new { error = "Produto indisponível ou estoque insuficiente." });
    var id = UserId(u); var item = await db.CartItems.SingleOrDefaultAsync(x => x.CustomerId == id && x.VariantId == input.VariantId);
    if (input.Quantity == 0) { if (item is not null) db.CartItems.Remove(item); }
    else if (item is null) db.CartItems.Add(new() { CustomerId = id, VariantId = input.VariantId, Quantity = input.Quantity });
    else item.Quantity = input.Quantity;
    await db.SaveChangesAsync(); return Results.Ok(await Cart(db, id));
}).RequireAuthorization();
app.MapPost("/api/checkout", async (CheckoutInput input, StoreDb db, ClaimsPrincipal u) => {
    if (string.IsNullOrWhiteSpace(input.Address) || input.Address.Trim().Length < 12 || input.Address.Length > 500) return Results.BadRequest(new { error = "Informe o endereço completo, incluindo CEP." });
    var id = UserId(u); var cart = await db.CartItems.Where(x => x.CustomerId == id).ToListAsync();
    if (cart.Count == 0) return Results.BadRequest(new { error = "Seu carrinho está vazio ou este pedido já foi confirmado." });
    var customer = (await db.Customers.FindAsync(id))!;
    var order = new Order { CustomerId = id, CustomerName = customer.Name, Address = input.Address.Trim() };
    foreach (var item in cart) {
        var v = await db.Variants.FindAsync(item.VariantId); var p = v is null ? null : await db.Products.FindAsync(v.ProductId);
        if (v is null || p is null || !p.Active || v.Stock < item.Quantity) return Results.Conflict(new { error = "Estoque alterado. Revise seu carrinho." });
        v.Stock -= item.Quantity;
        order.Items.Add(new() { VariantId = v.Id, ProductName = p.Name, Size = v.Size, Quantity = item.Quantity, UnitPrice = p.Price });
        order.Total += p.Price * item.Quantity;
    }
    db.Orders.Add(order); db.CartItems.RemoveRange(cart); await db.SaveChangesAsync();
    return Results.Ok(order);
}).RequireAuthorization();
app.MapGet("/api/orders", async (StoreDb db, ClaimsPrincipal u) => { var id = UserId(u); return await db.Orders.AsNoTracking().Include(x => x.Items).Where(x => x.CustomerId == id).OrderByDescending(x => x.Id).ToListAsync(); }).RequireAuthorization();
var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
admin.MapGet("/overview", async (StoreDb db) => new { customers = await db.Customers.CountAsync(x => !x.IsAdmin), products = await db.Products.CountAsync(), orders = await db.Orders.CountAsync(), revenue = (await db.Orders.Where(x => x.Status != "Cancelado").Select(x => x.Total).ToListAsync()).Sum() });
admin.MapGet("/products", async (StoreDb db) => await db.Products.AsNoTracking().Include(x => x.Variants).ToListAsync());
admin.MapGet("/orders", async (StoreDb db) => await db.Orders.AsNoTracking().Include(x => x.Items).OrderByDescending(x => x.Id).ToListAsync());
admin.MapGet("/customers", async (StoreDb db) => await db.Customers.Select(x => new { x.Id, x.Name, x.Email, x.IsAdmin }).ToListAsync());
admin.MapPost("/products", async (ProductInput input, StoreDb db) => await SaveProduct(input, null, db));
admin.MapPut("/products/{id:int}", async (int id, ProductInput input, StoreDb db) => await SaveProduct(input, id, db));
admin.MapPut("/orders/{id:int}/status", async (int id, StatusInput input, StoreDb db) => {
    var o = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id);
    if (o is null) return Results.NotFound();
    var next = o.Status switch { "Confirmado" => new[] { "Preparando", "Cancelado" }, "Preparando" => new[] { "Enviado", "Cancelado" }, "Enviado" => new[] { "Entregue" }, _ => Array.Empty<string>() };
    if (!next.Contains(input.Status)) return Results.BadRequest(new { error = "Transição de status não permitida." });
    if (input.Status == "Cancelado") foreach (var item in o.Items) { var v = await db.Variants.FindAsync(item.VariantId); if (v is not null) v.Stock += item.Quantity; }
    o.Status = input.Status; await db.SaveChangesAsync(); return Results.Ok(o);
});
app.MapFallbackToFile("index.html");
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
    if (!await db.Products.AnyAsync()) {
        var names = new[] { "District Essential Tee", "Concrete Oversized", "Midnight Hoodie", "Urban Cargo", "District Heavyweight", "Coordinates Tee" };
        var prices = new decimal[] { 129.90m, 159.90m, 289.90m, 249.90m, 319.90m, 149.90m };
        var categories = new[] { "Camisetas", "Camisetas", "Moletons", "Calças", "Moletons", "Camisetas" };
        for (int i = 0; i < names.Length; i++) db.Products.Add(new() { Name = names[i], Category = categories[i], Price = prices[i], Image = i is 2 or 4 ? "/assets/hoodie.svg" : i == 3 ? "/assets/cargo.svg" : "/assets/tee.svg", Description = "Criado para quem define o próprio caminho. Modelagem ampla, acabamento premium e identidade SICKO DISTRICT. Cor: preto. Coleção 01 / São Paulo.", Variants = new[] { "P", "M", "G", "GG" }.Select(s => new Variant { Size = s, Stock = 12 }).ToList() });
    }
    if (app.Environment.IsDevelopment() && !await db.Customers.AnyAsync(x => x.IsAdmin)) {
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<Customer>>();
        var c = new Customer { Name = "Administrador", Email = "admin@sicko.local", IsAdmin = true };
        c.PasswordHash = hasher.HashPassword(c, "District@2026"); db.Customers.Add(c);
    }
    await db.SaveChangesAsync();
}
app.Run();

static async Task SignIn(HttpContext ctx, Customer c) {
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, c.Id.ToString()), new Claim(ClaimTypes.Name, c.Name), new Claim(ClaimTypes.Role, c.IsAdmin ? "Admin" : "Cliente") };
    await ctx.SignInAsync("Cookies", new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")));
}
static async Task<object> Cart(StoreDb db, int userId) => await (from c in db.CartItems where c.CustomerId == userId join v in db.Variants on c.VariantId equals v.Id join p in db.Products on v.ProductId equals p.Id select new { c.VariantId, c.Quantity, v.Size, v.Stock, p.Name, p.Price, p.Image, p.Active }).ToListAsync();
static async Task<IResult> SaveProduct(ProductInput input, int? id, StoreDb db) {
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || string.IsNullOrWhiteSpace(input.Category) || input.Category.Length > 50 || input.Description is null || input.Description.Length > 2000 || input.Price <= 0 || input.Price > 100000 || input.Variants is null || input.Variants.Count is < 1 or > 8 || input.Variants.Any(v => !new[] { "P", "M", "G", "GG", "U" }.Contains(v.Size) || v.Stock < 0 || v.Stock > 100000) || input.Variants.Select(v => v.Size).Distinct().Count() != input.Variants.Count || !new[] { "/assets/tee.svg", "/assets/hoodie.svg", "/assets/cargo.svg" }.Contains(input.Image))
        return Results.BadRequest(new { error = "Revise nome, categoria, preço, imagem e estoque por tamanho." });
    var p = id.HasValue ? await db.Products.Include(x => x.Variants).SingleOrDefaultAsync(x => x.Id == id) : new Product();
    if (p is null) return Results.NotFound();
    if (id is null) db.Products.Add(p);
    p.Name = input.Name.Trim(); p.Category = input.Category.Trim(); p.Description = input.Description; p.Price = input.Price; p.Image = input.Image; p.Active = input.Active;
    // Variantes existentes são preservadas para manter referências do histórico.
    foreach (var v in p.Variants) if (!input.Variants.Any(x => x.Size == v.Size)) v.Stock = 0;
    foreach (var v in input.Variants) { var current = p.Variants.SingleOrDefault(x => x.Size == v.Size); if (current is null) p.Variants.Add(new() { Size = v.Size, Stock = v.Stock }); else current.Stock = v.Stock; }
    await db.SaveChangesAsync(); return Results.Ok(p);
}
