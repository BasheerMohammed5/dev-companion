using Serilog;
using MockTargetApi.Integration;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog with Custom DevCompanionAgent Sink
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.DevCompanionAgent(
        projectRoot: "d:\\work\\2026\\me\\dev-companion\\MockTargetApi",
        agentUrl: "http://localhost:5005"
    )
    .CreateLogger();

builder.Host.UseSerilog();

// Add Swagger services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure HTTP pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var products = new List<Product>
{
    new(1, "Laptop", 999.99m),
    new(2, "Smartphone", 499.99m),
    new(3, "Headphones", 79.99m)
};

// 1. Normal GET list
app.MapGet("/api/products", () => Results.Ok(products))
   .WithName("GetProducts")
   .WithOpenApi();

// 2. GET by ID with simulated SQL injection vulnerability
app.MapGet("/api/products/{id}", (string id) =>
{
    // If the input contains a single quote, simulate a SQL parsing exception
    if (id.Contains("'") || id.Contains("--"))
    {
        throw new InvalidOperationException("An error occurred while executing the query: System.Data.SqlClient.SqlException: Unclosed quotation mark after the character string 'OR 1=1 --'.");
    }

    if (int.TryParse(id, out var intId))
    {
        var product = products.FirstOrDefault(p => p.Id == intId);
        return product != null ? Results.Ok(product) : Results.NotFound();
    }

    return Results.BadRequest("Invalid product ID format. ID must be an integer.");
})
.WithName("GetProductById")
.WithOpenApi();

// 3. POST product with simulated boundary check crash
app.MapPost("/api/products", ([FromBody] ProductInput input) =>
{
    if (string.IsNullOrWhiteSpace(input.Name))
    {
        return Results.BadRequest("Product name is required.");
    }

    // Simulate developer forgetting defensive checks in a service layer, throwing 500 error
    if (input.Price <= 0)
    {
        throw new ArgumentException("Critical database violation: Product price must be greater than zero. Price provided: " + input.Price);
    }

    var newProduct = new Product(products.Count + 1, input.Name, input.Price);
    products.Add(newProduct);
    return Results.Created($"/api/products/{newProduct.Id}", newProduct);
})
.WithName("CreateProduct")
.WithOpenApi();

// 4. GET crash endpoint to test Live Error-Catch (NullReferenceException)
app.MapGet("/api/products/crash", () =>
{
    string? nullString = null;
    // This will throw NullReferenceException on line 87 (or matching line)
    int length = nullString!.Length; 
    return Results.Ok(length);
})
.WithName("TriggerCrash")
.WithOpenApi();

app.Run();

public record Product(int Id, string Name, decimal Price);
public record ProductInput(string Name, decimal Price);
