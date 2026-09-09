using FluentAssertions;

namespace StreetBiz.Infrastructure.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Infrastructure_must_not_reference_api()
    {
        var references = typeof(Infrastructure.DependencyInjection).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToArray();

        references.Should().NotContain(name =>
            name!.StartsWith("StreetBiz.API", StringComparison.Ordinal));
    }
}
