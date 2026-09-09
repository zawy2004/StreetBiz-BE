using FluentAssertions;

namespace StreetBiz.API.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Api_composition_root_references_required_layers()
    {
        var references = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToArray();

        references.Should().Contain("StreetBiz.Application");
        references.Should().Contain("StreetBiz.Infrastructure");
    }
}
