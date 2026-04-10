using System.Net;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using KanAuth.API.Middleware;
using KanAuth.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace KanAuth.Tests.Middleware;

public class ExceptionHandlingMiddlewareTests
{
    private static async Task<(int StatusCode, JsonElement Body)> InvokeAsync(
        Exception exception,
        string environment = "Development")
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var logger = Substitute.For<ILogger<ExceptionHandlingMiddleware>>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        RequestDelegate next = _ => throw exception;

        var middleware = new ExceptionHandlingMiddleware(next, logger, env);
        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var json = JsonDocument.Parse(body).RootElement;

        return (context.Response.StatusCode, json);
    }

    // ── Status code mapping ───────────────────────────────────────────────────

    [Fact]
    public async Task InvalidCredentialsException_Returns401()
    {
        var (status, _) = await InvokeAsync(new InvalidCredentialsException());

        status.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenExpiredException_Returns401()
    {
        var (status, _) = await InvokeAsync(new TokenExpiredException());

        status.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenReuseException_Returns401()
    {
        var (status, _) = await InvokeAsync(new TokenReuseException());

        status.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UserNotFoundException_Returns404()
    {
        var (status, _) = await InvokeAsync(new UserNotFoundException(Guid.NewGuid()));

        status.Should().Be((int)HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InvalidOperationException_Returns409()
    {
        var (status, _) = await InvokeAsync(new InvalidOperationException("conflict"));

        status.Should().Be((int)HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ValidationException_Returns400()
    {
        var failures = new[] { new ValidationFailure("Email", "Email is invalid.") };
        var (status, _) = await InvokeAsync(new ValidationException(failures));

        status.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnhandledException_Returns500()
    {
        var (status, _) = await InvokeAsync(new Exception("boom"));

        status.Should().Be((int)HttpStatusCode.InternalServerError);
    }

    // ── Response body ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Response_ContainsProblemDetailsFields()
    {
        var (_, body) = await InvokeAsync(new InvalidCredentialsException());

        body.GetProperty("status").GetInt32().Should().Be(401);
        body.GetProperty("title").GetString().Should().Be("Unauthorized");
        body.TryGetProperty("detail", out _).Should().BeTrue();
        body.TryGetProperty("type", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ValidationException_BodyContainsFieldErrors()
    {
        var failures = new[]
        {
            new ValidationFailure("Email", "Email is invalid."),
            new ValidationFailure("Password", "Too short.")
        };
        var (_, body) = await InvokeAsync(new ValidationException(failures));

        var detail = body.GetProperty("detail").GetString()!;
        detail.Should().Contain("Email").And.Contain("Password");
    }

    [Fact]
    public async Task UnhandledException_InProduction_HidesDetail()
    {
        var (_, body) = await InvokeAsync(new Exception("secret internal message"), "Production");

        var detail = body.GetProperty("detail").GetString();
        detail.Should().NotContain("secret internal message");
        detail.Should().Be("An unexpected error occurred.");
    }

    [Fact]
    public async Task UnhandledException_InDevelopment_ExposesDetail()
    {
        var (_, body) = await InvokeAsync(new Exception("detailed debug info"), "Development");

        var detail = body.GetProperty("detail").GetString();
        detail.Should().Be("detailed debug info");
    }

    // ── Content type ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Response_ContentTypeIsApplicationProblemJson()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var logger = Substitute.For<ILogger<ExceptionHandlingMiddleware>>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        RequestDelegate next = _ => throw new InvalidCredentialsException();
        var middleware = new ExceptionHandlingMiddleware(next, logger, env);
        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().Be("application/problem+json");
    }
}
