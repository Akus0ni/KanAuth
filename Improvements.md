 ---
  Code Review — KanAuth
                                                                                                                                                                                                                                               Here are the issues found, grouped by severity. No changes have been made.
                                                                                                                                                                                                                                               ---
  Bug / Correctness

  1. Hardcoded access token expiry in AuthResponse diverges from config

  In AuthService.IssueTokenPair (line 124) and AuthService.RefreshTokenAsync (line 100), the AuthResponse.AccessTokenExpiresAt field is built with a literal AddMinutes(15):

  // AuthService.cs:124
  return new AuthResponse(
      accessToken,
      refreshToken.Token,
      DateTime.UtcNow.AddMinutes(15),   // ← hardcoded
      ...

  But TokenService.GenerateAccessToken uses _jwt.AccessTokenExpiryMinutes from config. If someone changes the config setting, the JWT will actually expire at the new time, but the response body will still tell the client "15 minutes".
  The client will either refresh too early or too late. The fix is to pass the expiry from TokenService back to AuthService instead of recalculating it.

  ---
  2. No transaction between AddAsync(user) and AddAsync(refreshToken) in registration

  In AuthService.RegisterAsync:

  await _users.AddAsync(user, ct);           // ← commits to DB
  return await IssueTokenPair(user, ip, ct); // ← commits refresh token separately

  Each repository method calls SaveChangesAsync individually. If _refreshTokens.AddAsync throws (e.g., DB constraint), the user row is already committed. The registration appears to fail to the caller but the user now has an orphaned row
   in the database. They can't register again (email exists) and can't log in (no tokens). This affects both RegisterAsync and LoginAsync (login creates a new refresh token).

  ---
  Design / Correctness

  3. Domain exceptions logged at LogError level

  In ExceptionHandlingMiddleware.HandleExceptionAsync (line 37):

  _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

  This fires for every InvalidCredentialsException — i.e., every wrong password attempt. Expected business outcomes (bad credentials, expired token, user not found) are not errors; they're normal events. Logging them at Error floods logs
   and makes it hard to detect real errors. Domain exceptions should be LogWarning (or LogInformation); only the _ (unexpected) case warrants LogError.

  ---
  4. Double email normalization

  AuthService normalizes email to lowercase before calling the repository (lines 28, 43). The repository then normalizes it again (lines 21, 24 of UserRepository):

  // AuthService.cs:28
  var email = req.Email.ToLowerInvariant();
  ...
  await _users.EmailExistsAsync(email, ct);   // already lowercase

  // UserRepository.cs:24
  _db.Users.AnyAsync(u => u.Email == email.ToLowerInvariant(), ct);  // normalizes again

  The normalization should live in exactly one place. The redundancy means if either site is ever changed, they can diverge silently.

  ---
  5. AsNoTracking() + Update() generates a full-row UPDATE on every save

  UserRepository.GetByEmailAsync and GetByIdAsync use AsNoTracking(). The returned entity is detached from the EF Core change tracker. When UpdateAsync is later called, _db.Users.Update(user) reattaches it and marks every property as
  modified — EF Core generates UPDATE Users SET Email=..., PasswordHash=..., FirstName=..., ... WHERE Id=..., overwriting all columns even if only LastLoginAtUtc changed. The fix is to either remove AsNoTracking from the "for-update"
  queries or track only the changed properties.

  ---
  Interface / API Design

  6. ITokenService.ValidateAccessToken is dead code

  ValidateAccessToken is declared on ITokenService and implemented in TokenService, but it is never called anywhere in the application. JWT validation is handled entirely by the ASP.NET Core bearer middleware configured in Program.cs.
  The method exists on the interface with no caller — it should be removed or, if intended for a future feature (e.g., manual token introspection), that intent should be documented.

  ---
  7. PasswordHash has a public set on User

  // User.cs:7
  public string PasswordHash { get; set; } = string.Empty;

  Id and Email are private set, but PasswordHash is public. Any code can overwrite the hash directly, bypassing future business logic. It should be private set (changed only through a dedicated method like UpdatePassword(string
  newHash)).

  ---
  8. RefreshToken is a fully mutable object — inconsistent with User

  User uses a private constructor and a static factory (User.Create(...)) to enforce invariants on creation. RefreshToken has all public setters and is constructed inline in TokenService with an object initializer. The pattern is
  inconsistent. A factory or at minimum a structured constructor would make creation safer and more readable.

  ---
  Controller / API Layer

  9. Redundant claim lookup in AuthController.Me

  // AuthController.cs:61
  var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
      ?? User.FindFirstValue("sub")       // ← this fallback is unreachable by default
      ?? throw new ...);

  The default JwtSecurityTokenHandler automatically maps the sub JWT claim to ClaimTypes.NameIdentifier. The ?? User.FindFirstValue("sub") branch is dead unless someone adds MapInboundClaims = false to the JWT options. This should either
   be removed (if not needed) or MapInboundClaims = false should be set deliberately and documented.

  ---
  10. No [ProducesResponseType] attributes on AuthController

  The controller has no response type annotations. Swagger generates no response schemas for the endpoints, making it hard for consumers to understand what the API returns without manually testing it. Adding
  [ProducesResponseType(typeof(AuthResponse), 200)] etc. improves the generated OpenAPI spec.

  ---
  Missing

  11. No test project

  There is no test project in the solution. For a security-critical service (auth), this is the most significant gap. AuthService is well-structured for unit testing (all dependencies are interfaces), but no tests exist. At minimum,
  happy-path and failure-path unit tests for RegisterAsync, LoginAsync, and RefreshTokenAsync (especially the reuse detection path) would give confidence in the logic.

  ---
  Summary Table

  ┌─────┬──────────┬────────────────────────────────────┬─────────────────────────────────────────────────────────────────────────────┐
  │  #  │ Severity │              Location              │                                    Issue                                    │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 1   │ Bug      │ AuthService.cs:100,124             │ Hardcoded AddMinutes(15) diverges from JwtSettings.AccessTokenExpiryMinutes │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 2   │ Bug      │ AuthService.RegisterAsync          │ No transaction — user can be persisted without a refresh token              │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 3   │ Design   │ ExceptionHandlingMiddleware.cs:37  │ Domain exceptions logged at LogError instead of LogWarning                  │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 4   │ Design   │ AuthService.cs + UserRepository.cs │ Email normalized in two places                                              │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 5   │ Design   │ UserRepository.cs                  │ AsNoTracking + Update causes full-row UPDATE always                         │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 6   │ Design   │ ITokenService.cs / TokenService.cs │ ValidateAccessToken is unused dead code                                     │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 7   │ Design   │ User.cs:7                          │ PasswordHash has public set — inconsistent encapsulation                    │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 8   │ Design   │ RefreshToken.cs                    │ No factory / private constructor, unlike User                               │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 9   │ Minor    │ AuthController.cs:61               │ Redundant ?? FindFirstValue("sub") fallback                                 │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 10  │ Minor    │ AuthController.cs                  │ No [ProducesResponseType] — poor Swagger docs                               │
  ├─────┼──────────┼────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────┤
  │ 11  │ Gap      │ —                                  │ No test project                                                             │
  └─────┴──────────┴────────────────────────────────────┴─────────────────────────────────────────────────────────────────────────────┘

  ---