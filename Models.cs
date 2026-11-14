namespace SickoDistrict;
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
}
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string Image { get; set; } = "/assets/tee.svg";
    public bool Active { get; set; } = true;
    public List<Variant> Variants { get; set; } = [];
}
public class Variant
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Size { get; set; } = "M";
    public int Stock { get; set; }
    public long Version { get; set; }
}
public class CartItem
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int VariantId { get; set; }
    public int Quantity { get; set; }
}
public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Address { get; set; } = "";
    public string Status { get; set; } = "Confirmado";
    public string Payment { get; set; } = "Aprovado — simulação acadêmica";
    public decimal Total { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public long Version { get; set; }
}
public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int VariantId { get; set; }
    public string ProductName { get; set; } = "";
    public string Size { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
public record Credentials(string Email, string Password, string? Name);
public record CartInput(int VariantId, int Quantity);
public record CheckoutInput(string Address);
public record StatusInput(string Status);
public record VariantInput(string Size, int Stock);
public record ProductInput(string Name, string Category, string Description, decimal Price, string Image, bool Active, List<VariantInput> Variants);
