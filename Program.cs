using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
// Middleware order: Error handling first, then authentication, then logging
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<AuthenticationMiddleware>();
app.UseMiddleware<LoggingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");

// In-memory user storage (thread-safe using ConcurrentDictionary)
var users = new ConcurrentDictionary<int, User>();
users.TryAdd(1, new User { Id = 1, FirstName = "John", LastName = "Doe", Email = "john.doe@techhive.com", Department = "Engineering" });
users.TryAdd(2, new User { Id = 2, FirstName = "Jane", LastName = "Smith", Email = "jane.smith@techhive.com", Department = "HR" });
var nextUserId = 3;
var nextUserIdLock = new object();

// GET: Retrieve all users
app.MapGet("/api/users", () =>
{
    return Results.Ok(users.Values.ToList());
})
.WithName("GetAllUsers")
.Produces<List<User>>(StatusCodes.Status200OK);

// GET: Retrieve a specific user by ID
app.MapGet("/api/users/{id}", (int id) =>
{
    if (!users.TryGetValue(id, out var user))
        return Results.NotFound(new { message = $"User with ID {id} not found." });
    
    return Results.Ok(user);
})
.WithName("GetUserById")
.Produces<User>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound);

// POST: Create a new user
app.MapPost("/api/users", (CreateUserRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
        return Results.BadRequest(new { message = "FirstName and LastName are required." });

    int userId;
    lock (nextUserIdLock)
    {
        userId = nextUserId++;
    }

    var newUser = new User
    {
        Id = userId,
        FirstName = request.FirstName,
        LastName = request.LastName,
        Email = request.Email,
        Department = request.Department
    };

    users.TryAdd(userId, newUser);
    return Results.Created($"/api/users/{newUser.Id}", newUser);
})
.WithName("CreateUser")
.Produces<User>(StatusCodes.Status201Created)
.Produces(StatusCodes.Status400BadRequest);

// PUT: Update an existing user
app.MapPut("/api/users/{id}", (int id, UpdateUserRequest request) =>
{
    if (!users.TryGetValue(id, out var user))
        return Results.NotFound(new { message = $"User with ID {id} not found." });

    if (!string.IsNullOrWhiteSpace(request.FirstName))
        user.FirstName = request.FirstName;
    if (!string.IsNullOrWhiteSpace(request.LastName))
        user.LastName = request.LastName;
    if (!string.IsNullOrWhiteSpace(request.Email))
        user.Email = request.Email;
    if (!string.IsNullOrWhiteSpace(request.Department))
        user.Department = request.Department;

    return Results.Ok(user);
})
.WithName("UpdateUser")
.Produces<User>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest);

// DELETE: Remove a user by ID
app.MapDelete("/api/users/{id}", (int id) =>
{
    if (!users.TryRemove(id, out _))
        return Results.NotFound(new { message = $"User with ID {id} not found." });

    return Results.Ok(new { message = $"User with ID {id} has been deleted successfully." });
})
.WithName("DeleteUser")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound);

app.Run();

// Middleware classes

// 1. Error Handling Middleware - catches exceptions and returns consistent JSON responses
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unhandled exception occurred");

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var errorResponse = new { error = "Internal server error." };
            await context.Response.WriteAsJsonAsync(errorResponse);
        }
    }
}

// 2. Authentication Middleware - validates tokens from incoming requests
public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    public AuthenticationMiddleware(RequestDelegate next, ILogger<AuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip authentication for OpenAPI/Swagger endpoints
        if (context.Request.Path.StartsWithSegments("/openapi") ||
            context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context);
            return;
        }

        // Check for Authorization header
        if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader) ||
            !authHeader.ToString().StartsWith("Bearer "))
        {
            _logger.LogWarning("Missing or invalid Authorization header");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Token required." });
            return;
        }

        // Extract token (simplified - in production, validate JWT properly)
        var token = authHeader.ToString().Substring("Bearer ".Length).Trim();

        // For demo purposes, accept any non-empty token
        // In production, validate JWT token here
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Empty token provided");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Invalid token." });
            return;
        }

        // Token is valid, continue to next middleware
        await _next(context);
    }
}

// 3. Logging Middleware - logs HTTP requests and responses
public class LoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(RequestDelegate next, ILogger<LoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Log incoming request
        _logger.LogInformation("Request: {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        // Capture the original response body stream
        var originalBodyStream = context.Response.Body;

        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        try
        {
            await _next(context);

            // Log response status code
            _logger.LogInformation("Response: {StatusCode} for {Method} {Path}",
                context.Response.StatusCode,
                context.Request.Method,
                context.Request.Path);

            // Copy the response back to the original stream
            responseBody.Seek(0, SeekOrigin.Begin);
            await responseBody.CopyToAsync(originalBodyStream);
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }
}

// User model
public class User
{
    public int Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? Email { get; set; }
    public string? Department { get; set; }
}

// Request DTOs for creating and updating users
public record CreateUserRequest(string FirstName, string LastName, string? Email = null, string? Department = null);

public record UpdateUserRequest(string? FirstName = null, string? LastName = null, string? Email = null, string? Department = null);
