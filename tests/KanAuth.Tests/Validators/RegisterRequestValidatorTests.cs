using FluentAssertions;
using FluentValidation.TestHelper;
using KanAuth.Application.DTOs.Requests;
using KanAuth.Application.Validators;

namespace KanAuth.Tests.Validators;

public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _sut = new();

    private static RegisterRequest Valid() =>
        new("Alice", "Smith", "alice@example.com", "Password1!");

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var result = _sut.TestValidate(Valid());

        result.IsValid.Should().BeTrue();
    }

    // ── FirstName ─────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyFirstName_FailsValidation()
    {
        var result = _sut.TestValidate(Valid() with { FirstName = "" });

        result.ShouldHaveValidationErrorFor(x => x.FirstName);
    }

    [Fact]
    public void FirstNameOver100Chars_FailsValidation()
    {
        var result = _sut.TestValidate(Valid() with { FirstName = new string('A', 101) });

        result.ShouldHaveValidationErrorFor(x => x.FirstName);
    }

    // ── LastName ──────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyLastName_FailsValidation()
    {
        var result = _sut.TestValidate(Valid() with { LastName = "" });

        result.ShouldHaveValidationErrorFor(x => x.LastName);
    }

    // ── Email ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyEmail_FailsValidation()
    {
        var result = _sut.TestValidate(Valid() with { Email = "" });

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing@")]
    [InlineData("@nodomain.com")]
    public void InvalidEmail_FailsValidation(string email)
    {
        var result = _sut.TestValidate(Valid() with { Email = email });

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    // ── Password ──────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyPassword_FailsValidation()
    {
        var result = _sut.TestValidate(Valid() with { Password = "" });

        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void PasswordUnder8Chars_FailsValidation()
    {
        var result = _sut.TestValidate(Valid() with { Password = "Ab1!" });

        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void PasswordWithoutUppercase_FailsWithCorrectMessage()
    {
        var result = _sut.TestValidate(Valid() with { Password = "password1!" });

        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password must contain at least one uppercase letter.");
    }

    [Fact]
    public void PasswordWithoutDigit_FailsWithCorrectMessage()
    {
        var result = _sut.TestValidate(Valid() with { Password = "Password!" });

        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password must contain at least one digit.");
    }

    [Fact]
    public void PasswordWithoutSpecialChar_FailsWithCorrectMessage()
    {
        var result = _sut.TestValidate(Valid() with { Password = "Password1" });

        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password must contain at least one special character.");
    }

    [Fact]
    public void PasswordOver128Chars_FailsValidation()
    {
        var result = _sut.TestValidate(Valid() with { Password = "Aa1!" + new string('x', 125) });

        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Abcdefg1!")]          // exactly 9 chars — passes
    [InlineData("Aa1!Aa1!")]           // exactly 8 chars — passes
    public void PasswordAtMinimumLength_Passes(string password)
    {
        var result = _sut.TestValidate(Valid() with { Password = password });

        result.ShouldNotHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void MultiplePasswordViolations_ReportsAllErrors()
    {
        // lowercase only, no digit, no special — three violations
        var result = _sut.TestValidate(Valid() with { Password = "alllowercase" });

        result.Errors.Where(e => e.PropertyName == nameof(RegisterRequest.Password))
            .Should().HaveCountGreaterThanOrEqualTo(3);
    }
}
