using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Operations;

namespace AquaCulture.Tests;

public class OperationsTests
{
    private static Inventory FeedStore()
    {
        var inv = new Inventory().Add(new InventoryItem("GROWER-2", "Grower feed 2 mm", ItemCategory.Feed) { LeadTimeDays = 7, SafetyDays = 5 });
        inv.Receive(new StockLot("GROWER-2", "G2-A", new DateOnly(2026, 6, 1), 1000, 16_000, new DateOnly(2026, 12, 1)));
        inv.Receive(new StockLot("GROWER-2", "G2-B", new DateOnly(2026, 6, 10), 1000, 16_500, new DateOnly(2026, 9, 1)));    // expires first
        return inv;
    }

    [Fact]
    public void Issues_go_first_expiring_first_out_and_land_on_the_cycle_with_lot_and_cost()
    {
        var inv = FeedStore();
        var cycle = new CultureCycle { Id = "C1", PondId = "P-01", Species = SpeciesCatalog.Vaname, StockingDate = new DateOnly(2026, 6, 1) };
        var used = inv.IssueFeed("GROWER-2", 1200, new DateOnly(2026, 6, 20), cycle);
        Assert.Equal(["G2-B", "G2-A"], used.Select(u => u.Lot.LotCode));
        Assert.Equal(1000, used[0].Quantity);
        Assert.Equal(800, inv.OnHand("GROWER-2"));
        Assert.Equal(800 * 16_000, inv.Value("GROWER-2"));
        Assert.Equal(2, cycle.Feedings.Count);
        Assert.Equal(16_500, cycle.Feedings[0].PricePerKg);
        Assert.Equal("G2-B", cycle.Feedings[0].FeedId);
        Assert.Throws<InvalidOperationException>(() => inv.Issue("GROWER-2", 900, new DateOnly(2026, 6, 21)));
    }

    [Fact]
    public void Expired_lots_are_skipped_and_written_off()
    {
        var inv = FeedStore();
        Assert.Equal(1000, inv.OnHand("GROWER-2", new DateOnly(2026, 10, 1)));         // G2-B has expired
        var used = inv.Issue("GROWER-2", 100, new DateOnly(2026, 10, 1));
        Assert.Equal("G2-A", used.Single().Lot.LotCode);
        Assert.Equal(1000 * 16_500, inv.WriteOffExpired(new DateOnly(2026, 10, 1)));
        Assert.Contains(inv.Movements, m => m.Kind == "write-off");
    }

    [Fact]
    public void Reorder_point_from_history_or_from_the_feeding_plan()
    {
        var inv = FeedStore();
        for (int d = 0; d < 14; d++) inv.Issue("GROWER-2", 40, new DateOnly(2026, 6, 1).AddDays(d));
        var today = new DateOnly(2026, 6, 14);
        var r = inv.Reorder("GROWER-2", today);
        Assert.Equal(40, r.DailyUse, 6);
        Assert.Equal(40 * 12, r.ReorderPoint, 6);
        Assert.Equal(2000 - 14 * 40, r.OnHand, 6);
        Assert.False(r.OrderNow);
        var planned = inv.Reorder("GROWER-2", today, plannedDailyUse: 150);            // the stock is growing: the plan says 150 kg/day
        Assert.True(planned.OrderNow);
        Assert.Equal(150 * (30 + 12) - 1440, planned.SuggestedOrder, 0);
        Assert.Equal(9, planned.DaysOfCover);
    }

    [Fact]
    public void Purchase_orders_receive_into_lots_and_compare_suppliers()
    {
        var inv = new Inventory().Add(new InventoryItem("MOLASSES", "Molasses", ItemCategory.Other));
        var po = new PurchaseOrder { Number = "PO-001", Supplier = "CV Tebu", Date = new DateOnly(2026, 6, 1) };
        po.Lines.Add(new PoLine("MOLASSES", 1000, 4_000));
        Assert.Equal(4_440_000, po.Total);
        Assert.Throws<InvalidOperationException>(() => po.Receive(inv, "MOLASSES", 10, "M-1", new DateOnly(2026, 6, 2)));
        po.Place();
        po.Receive(inv, "MOLASSES", 600, "M-1", new DateOnly(2026, 6, 5));
        Assert.Equal(PoStatus.PartiallyReceived, po.Status);
        po.Receive(inv, "MOLASSES", 400, "M-2", new DateOnly(2026, 6, 9));
        Assert.Equal(PoStatus.Received, po.Status);
        Assert.Equal(1000, inv.OnHand("MOLASSES"));
        Assert.Throws<InvalidOperationException>(() => po.Receive(inv, "MOLASSES", 100, "M-3", new DateOnly(2026, 6, 10)));

        var po2 = new PurchaseOrder { Number = "PO-002", Supplier = "UD Manis", Date = new DateOnly(2026, 7, 1) };
        po2.Lines.Add(new PoLine("MOLASSES", 500, 3_600));
        var cmp = Purchasing.CompareSuppliers([po, po2], "MOLASSES");
        Assert.Equal("UD Manis", cmp[0].Supplier);
    }

    [Fact]
    public void Indonesian_payroll_overtime_bpjs_bonus_and_thr()
    {
        // overtime on 3 days: 1 h, 2 h, 3 h → (1.5) + (1.5 + 2) + (1.5 + 4) = 10.5 × hourly; hourly = 5 190 000 / 173 = 30 000
        Assert.Equal(10.5 * 30_000, Payroll.OvertimePay(5_190_000, [1, 2, 3]), 6);
        Assert.Equal(5_190_000, Payroll.Thr(5_190_000, new DateOnly(2024, 1, 1), new DateOnly(2026, 3, 20)));
        Assert.Equal(5_190_000 * 6 / 12.0, Payroll.Thr(5_190_000, new DateOnly(2025, 9, 15), new DateOnly(2026, 3, 20)), 6);
        Assert.Equal(0, Payroll.Thr(5_190_000, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 20)));

        var tech = new Employee("E1", "Budi", "Pond technician", PayBasis.Monthly, 5_190_000, new DateOnly(2024, 1, 1)) { HarvestBonusPerKg = 100 };
        var slip = Payroll.Monthly(tech, new Timesheet("E1", 25, [1, 2, 3]), harvestKg: 12_000);
        Assert.Equal(1_200_000, slip.HarvestBonus);
        // employee: JHT 2 % + JP 1 % + health 1 % of 5.19 M = 4 % → 207 600
        Assert.Equal(207_600, slip.EmployeeDeductions);
        Assert.Equal(slip.Gross - 207_600, slip.NetPay);
        // employer: JHT 3.7 + JKK 0.54 + JKM 0.3 + JP 2 + health 4 = 10.54 % → 547 026
        Assert.Equal(547_026, slip.EmployerContributions);

        var rich = Payroll.Monthly(tech with { Rate = 20_000_000, HarvestBonusPerKg = 0 }, new Timesheet("E1", 25, []));
        Assert.Equal(20_000_000 * 0.02 + 10_042_300 * 0.01 + 12_000_000 * 0.01, rich.EmployeeDeductions, 0);   // JP and health are capped

        var daily = new Employee("E2", "Sari", "Feeder", PayBasis.Daily, 120_000, new DateOnly(2025, 1, 1)) { ParticipatesInBpjs = false };
        var d = Payroll.Monthly(daily, new Timesheet("E2", 22, []));
        Assert.Equal(22 * 120_000, d.Gross);
        Assert.Equal(0, d.EmployerContributions);
        Assert.Equal((slip.EmployerCost + d.EmployerCost) / 12_000, Payroll.LabourCostPerKg([slip, d], 12_000), 6);
    }
}
