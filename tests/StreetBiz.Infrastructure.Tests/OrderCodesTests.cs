using System.Text.RegularExpressions;
using StreetBiz.Infrastructure.Persistence.Repositories;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>The order number a customer reads to the stall: short, readable, dated in Vietnam.</summary>
public sealed class OrderCodesTests
{
    [Fact]
    public void A_code_is_DH_the_date_in_Vietnam_and_six_readable_characters()
    {
        var code = OrderCodes.New(new DateTime(2026, 11, 3, 2, 0, 0, DateTimeKind.Utc));

        Assert.Matches(new Regex("^DH-261103-[23456789ABCDEFGHJKMNPQRSTVWXYZ]{6}$"), code);
        Assert.Equal(16, code.Length);
    }

    [Fact]
    public void The_date_is_the_Vietnamese_one_even_late_in_the_UTC_evening()
    {
        // 18:30 UTC on 2 November is already 01:30 on 3 November in Hà Nội.
        var code = OrderCodes.New(new DateTime(2026, 11, 2, 18, 30, 0, DateTimeKind.Utc));

        Assert.StartsWith("DH-261103-", code);
    }

    [Fact]
    public void Characters_that_are_easy_to_misread_never_appear()
    {
        Assert.DoesNotContain('0', OrderCodes.Alphabet);
        Assert.DoesNotContain('O', OrderCodes.Alphabet);
        Assert.DoesNotContain('1', OrderCodes.Alphabet);
        Assert.DoesNotContain('I', OrderCodes.Alphabet);
        Assert.DoesNotContain('L', OrderCodes.Alphabet);
        Assert.Equal(OrderCodes.Alphabet.Length, OrderCodes.Alphabet.Distinct().Count());
    }

    [Fact]
    public void The_random_part_comes_from_the_picker_it_is_given()
    {
        var picks = new Queue<int>([0, 1, 2, 27, 28, 29]);

        var code = OrderCodes.New(new DateTime(2026, 11, 3, 2, 0, 0, DateTimeKind.Utc), _ => picks.Dequeue());

        Assert.Equal("DH-261103-234XYZ", code);
    }

    [Fact]
    public void Codes_fit_the_order_code_column_and_do_not_repeat_in_practice()
    {
        var now = new DateTime(2026, 11, 3, 2, 0, 0, DateTimeKind.Utc);
        var codes = Enumerable.Range(0, 5_000).Select(_ => OrderCodes.New(now)).ToList();

        Assert.All(codes, c => Assert.True(c.Length <= 30));
        // 30^6 ≈ 729M codes a day: 5,000 of them collide about 1.7% of the time (birthday bound),
        // which CommerceRepository absorbs by drawing again and the unique index backs up. Asking
        // for zero repeats made this test fail about one run in sixty; more than two repeated
        // codes (~1 in a million by chance) would mean the random part is broken.
        Assert.True(codes.Count - codes.Distinct().Count() <= 2);
    }
}
