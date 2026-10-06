using FluentAssertions;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Tests;

public sealed class VietnameseMoneyWordsTests
{
    [Theory]
    // The amounts the demo seed and the ward rates actually produce.
    [InlineData(1_040_000, "Một triệu không trăm bốn mươi nghìn đồng")]
    [InlineData(1_540_000, "Một triệu năm trăm bốn mươi nghìn đồng")]
    [InlineData(2_630_000, "Hai triệu sáu trăm ba mươi nghìn đồng")]
    [InlineData(1_980_000, "Một triệu chín trăm tám mươi nghìn đồng")]
    [InlineData(500_000, "Năm trăm nghìn đồng")]
    [InlineData(1_000_000, "Một triệu đồng")]
    [InlineData(750_000, "Bảy trăm năm mươi nghìn đồng")]
    [InlineData(25_000, "Hai mươi lăm nghìn đồng")]
    // The irregular readings.
    [InlineData(0, "Không đồng")]
    [InlineData(5, "Năm đồng")]
    [InlineData(10, "Mười đồng")]
    [InlineData(11, "Mười một đồng")]
    [InlineData(15, "Mười lăm đồng")]
    [InlineData(21, "Hai mươi mốt đồng")]
    [InlineData(24, "Hai mươi tư đồng")]
    [InlineData(55, "Năm mươi lăm đồng")]
    [InlineData(105, "Một trăm lẻ năm đồng")]
    [InlineData(110, "Một trăm mười đồng")]
    [InlineData(1_005, "Một nghìn không trăm lẻ năm đồng")]
    [InlineData(1_000_015, "Một triệu không trăm mười lăm đồng")]
    [InlineData(2_000_500, "Hai triệu năm trăm đồng")]
    [InlineData(50_000_000, "Năm mươi triệu đồng")]
    [InlineData(1_000_000_000, "Một tỷ đồng")]
    [InlineData(1_200_300_004, "Một tỷ hai trăm triệu ba trăm nghìn không trăm lẻ bốn đồng")]
    public void Reads_the_amount_the_way_a_Vietnamese_receipt_writes_it(long amount, string expected) =>
        VietnameseMoneyWords.Of(amount).Should().Be(expected);

    [Theory]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void Refuses_what_cannot_be_a_VND_amount(double amount)
    {
        var act = () => VietnameseMoneyWords.Of((decimal)amount);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
