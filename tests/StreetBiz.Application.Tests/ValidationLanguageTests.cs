using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace StreetBiz.Application.Tests;

/// <summary>
/// A rule without its own message falls back to FluentValidation's built-in text; once the
/// application is registered that text is Vietnamese, never English.
/// </summary>
public sealed class ValidationLanguageTests
{
    private sealed record Sample(string Name, decimal? Amount);

    [Fact]
    public void Built_in_messages_are_vietnamese_once_the_application_is_registered()
    {
        new ServiceCollection().AddApplication();
        var validator = new InlineValidator<Sample>();
        validator.RuleFor(x => x.Name).NotEmpty();
        validator.RuleFor(x => x.Amount).NotNull();

        var messages = validator.Validate(new Sample("", null)).Errors.Select(e => e.ErrorMessage).ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, m => Assert.DoesNotContain("must not be", m));
        Assert.Contains(messages, m => m.Contains("không được rỗng") || m.Contains("phải có giá trị"));
    }
}
