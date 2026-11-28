using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentValidation;
using StreetBiz.Application.Features.PlatformAdministration;

namespace StreetBiz.Application.Tests;

/// <summary>
/// Every FluentValidation validator in the Application layer: it must build, and it must answer the
/// emptiest request the API can hand it (empty strings, zeros, empty lists; model binding already
/// refuses a missing non-nullable field) and a fully filled one without throwing. A rule that
/// dereferences a missing value would otherwise turn a bad request into a 500 instead of a 400.
/// </summary>
public sealed class ValidatorSweepTests
{
    private static readonly Assembly Application = typeof(DecideOrderComplaintCommandValidator).Assembly;

    public static IEnumerable<object[]> Validators() =>
        Application.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && t.BaseType is { IsGenericType: true } b
                        && b.GetGenericTypeDefinition() == typeof(AbstractValidator<>)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName)
            .Select(t => new object[] { t.Name, t });

    [Fact]
    public void There_are_validators_to_sweep() => Assert.True(Validators().Count() > 150);

    [Theory]
    [MemberData(nameof(Validators))]
    public void Never_throws_on_a_blank_or_a_filled_request(string name, Type validatorType)
    {
        var validator = (IValidator)Activator.CreateInstance(validatorType)!;
        var target = validatorType.BaseType!.GetGenericArguments()[0];

        var blank = Build(target, 0, empty: true);
        var filled = Build(target, 0);

        foreach (var instance in new[] { blank, filled })
        {
            var context = (IValidationContext)Activator.CreateInstance(typeof(ValidationContext<>).MakeGenericType(target), instance)!;
            var ex = Record.Exception(() => validator.Validate(context));
            Assert.True(ex is null, $"{name} threw {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    /// <summary>An instance with every constructor argument / property set to a plausible value.</summary>
    private static object? Build(Type t, int depth, bool empty = false)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (empty && Nullable.GetUnderlyingType(t) is not null) return null;
        if (u == typeof(string)) return empty ? "" : "a";
        if (u == typeof(bool)) return !empty;
        if (u == typeof(Guid)) return Guid.NewGuid();
        if (u == typeof(DateOnly)) return new DateOnly(2026, 11, 1);
        if (u == typeof(DateTime)) return new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        if (u == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        if (u == typeof(TimeOnly)) return new TimeOnly(8, 0);
        if (u == typeof(TimeSpan)) return TimeSpan.FromMinutes(1);
        if (u.IsEnum) return Enum.GetValues(u).GetValue(0);
        if (u.IsPrimitive || u == typeof(decimal)) return Convert.ChangeType(empty ? 0 : 1, u);
        if (u == typeof(byte[])) return new byte[] { 1 };
        if (u == typeof(Stream)) return new MemoryStream([1]);
        if (depth > 3 || (u.IsInterface || u.IsAbstract) && !u.IsArray && !typeof(IEnumerable).IsAssignableFrom(u)) return null;
        if (u.IsArray)
        {
            var element = u.GetElementType()!;
            var array = Array.CreateInstance(element, empty ? 0 : 1);
            if (!empty) array.SetValue(Build(element, depth + 1), 0);
            return array;
        }
        if (u.IsGenericType && typeof(IEnumerable).IsAssignableFrom(u))
        {
            var args = u.GetGenericArguments();
            if (args.Length == 2)
            {
                var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                if (empty) return dict;
                var key = Build(args[0], depth + 1);
                if (key is not null) dict[key] = Build(args[1], depth + 1);
                return dict;
            }
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(args[0]))!;
            if (!empty) list.Add(Build(args[0], depth + 1));
            return list;
        }
        var ctor = u.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) return RuntimeHelpers.GetUninitializedObject(u);
        try
        {
            return ctor.Invoke(ctor.GetParameters().Select(p => Build(p.ParameterType, depth + 1, empty)).ToArray());
        }
        catch (TargetInvocationException)
        {
            return RuntimeHelpers.GetUninitializedObject(u);
        }
    }
}
