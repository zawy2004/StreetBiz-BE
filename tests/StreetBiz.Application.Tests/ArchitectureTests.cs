using FluentAssertions;

namespace StreetBiz.Application.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Application_must_not_reference_outer_layers()
    {
        var references = typeof(Application.DependencyInjection).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToArray();

        references.Should().NotContain(name =>
            name!.StartsWith("StreetBiz.Infrastructure", StringComparison.Ordinal) ||
            name.StartsWith("StreetBiz.API", StringComparison.Ordinal));
    }
}
