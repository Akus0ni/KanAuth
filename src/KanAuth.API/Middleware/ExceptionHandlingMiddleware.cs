using System.Text.Json;
using FluentValidation;
using KanAuth.Domain.Exceptions;

namespace KanAuth.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, title, detail) = exception switch
        {
            InvalidCredentialsException => (401, "Unauthorized", exception.Message),
            TokenExpiredException => (401, "Unauthorized", exception.Message),
            TokenReuseException => (401, "Unauthorized", exception.Message),
            UserNotFoundException => (404, "Not Found", exception.Message),
            ValidationException ve => (400, "Validation Failed", FormatValidationErrors(ve)),
            InvalidOperationException => (409, "Conflict", exception.Message),
            _ => (500, "Internal Server Error", _env.IsProduction()
                ? "An unexpected error occurred."
                : exception.Message)
        };

        if (statusCode == 500)
            _logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        else
            _logger.LogWarning("Request failed with {StatusCode}: {Message} on {Method} {Path}",
                statusCode, exception.Message, context.Request.Method, context.Request.Path);

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;

        var problem = new
        {
            type = $"https://httpstatuses.com/{statusCode}",
            title,
            status = statusCode,
            detail,
            instance = context.Request.Path.Value
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static string FormatValidationErrors(ValidationException ve) =>
        string.Join("; ", ve.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}"));
}

public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseExceptionHandlingMiddleware(this IApplicationBuilder app) =>
        app.UseMiddleware<ExceptionHandlingMiddleware>();
}
