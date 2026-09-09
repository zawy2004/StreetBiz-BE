using FluentAssertions;
using StreetBiz.Domain.Common;

namespace StreetBiz.Domain.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_must_not_reference_outer_layers_or_frameworks()
    {
        var references = AssemblyReference.Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToArray();

        references.Should().NotContain(name =>
            name!.StartsWith("StreetBiz.Application", StringComparison.Ordinal) ||
            name.StartsWith("StreetBiz.Infrastructure", StringComparison.Ordinal) ||
            name.StartsWith("StreetBiz.API", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
