using Tickets.Orders;
using Tickets.Tiers;
using Xunit;

namespace Tickets.UnitTests;

public class DtoTests
{
    [Fact]
    public void OrderDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            TierId = Guid.NewGuid(),
            TierCode = "S-1",
            Reference = "BUYER-1",
            Tickets = 3,
            TotalCents = 277_500,
            Status = OrderStatus.Sold,
            TicketNumber = "ET-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = OrderDto.From(order);

        Assert.Equal(order.Id, dto.Id);
        Assert.Equal(order.OwnerId, dto.OwnerId);
        Assert.Equal("Sold", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("ET-1", dto.TicketNumber);
    }

    [Fact]
    public void TierDto_MapsTheScarceNumber()
    {
        var tier = new Tier { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Section = "o", Venue = "d", TicketsTotal = 10, TicketsLeft = 4 };
        var dto = TierDto.From(tier);
        Assert.Equal(10, dto.TicketsTotal);
        Assert.Equal(4, dto.TicketsLeft);
    }
}
