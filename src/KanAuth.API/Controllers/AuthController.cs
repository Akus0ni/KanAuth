using System.Security.Claims;
using KanAuth.Application.DTOs.Requests;
using KanAuth.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanAuth.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth)
    {
        _auth = auth;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest req,
        CancellationToken ct)
    {
        var response = await _auth.RegisterAsync(req, GetClientIp(), ct);
        return CreatedAtAction(nameof(Me), response);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest req,
        CancellationToken ct)
    {
        var response = await _auth.LoginAsync(req, GetClientIp(), ct);
        return Ok(response);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        [FromBody] RefreshTokenRequest req,
        CancellationToken ct)
    {
        await _auth.LogoutAsync(req.RefreshToken, GetClientIp(), ct);
        return NoContent();
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest req,
        CancellationToken ct)
    {
        var response = await _auth.RefreshTokenAsync(req.RefreshToken, GetClientIp(), ct);
        return Ok(response);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new InvalidOperationException("User ID claim missing."));

        var user = await _auth.GetCurrentUserAsync(userId, ct);
        return Ok(user);
    }

    private string GetClientIp()
    {
        if (Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            var ip = forwarded.ToString().Split(',').FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(ip)) return ip;
        }
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
