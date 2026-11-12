using AquaCulture.Core;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Hosting;

namespace AquaCulture.Operations;

// ─────────────────────────────── inventory ───────────────────────────────

public enum ItemCategory { Feed, Probiotic, Chemical, Medicine, Fuel, Seed, Equipment, Other }

/// <summary>A stock-keeping item.</summary>
public sealed record InventoryItem(string Sku, string Name, ItemCategory Category, string Unit = "kg")
{
    /// <summary>Supplier lead time (days) and safety stock (days of use) for the reorder point.</summary>
    public int LeadTimeDays { get; init; } = 7;
    public int SafetyDays { get; init; } = 5;
}

/// <summary>A received lot of an item.</summary>
public sealed record StockLot(string Sku, string LotCode, DateOnly Received, double Quantity, double UnitCost, DateOnly? Expiry = null, string Supplier = "")
{
    public double Remaining { get; set; } = Quantity;
}

/// <summary>A stock movement (receipt, issue to a cycle or department, adjustment, write-off).</summary>
public sealed record StockMovement(DateOnly Date, string Sku, string LotCode, double Quantity, double UnitCost, string Kind, string Reference = "");

/// <summary>Reorder signal for an item.</summary>
public sealed record ReorderAdvice(InventoryItem Item, double OnHand, double DailyUse, double ReorderPoint, double SuggestedOrder, int DaysOfCover, bool OrderNow);

/// <summary>
/// Lot-tracked inventory: receipts create lots; issues consume lots first-expiring-first-out (then FIFO); feed issued to a culture cycle is
/// logged on the cycle with the lot code as feed id and the lot cost as price — so feed costs, FCR and EPCIS traceability all see the same lots.
/// </summary>
public sealed class Inventory
{
    private readonly Dictionary<string, InventoryItem> _items = [];
    private readonly List<StockLot> _lots = [];
    private readonly List<StockMovement> _movements = [];

    public IReadOnlyCollection<InventoryItem> Items => _items.Values;
    public IReadOnlyList<StockLot> Lots => _lots;
    public IReadOnlyList<StockMovement> Movements => _movements;

    public Inventory Add(InventoryItem item) { _items[item.Sku] = item; return this; }
    public InventoryItem Item(string sku) => _items.TryGetValue(sku, out var i) ? i : throw new KeyNotFoundException($"Unknown SKU {sku}.");

    public StockLot Receive(StockLot lot, string reference = "")
    {
        _ = Item(lot.Sku);
        if (lot.Quantity <= 0) throw new ArgumentException("Quantity must be positive.");
        _lots.Add(lot);
        _movements.Add(new StockMovement(lot.Received, lot.Sku, lot.LotCode, lot.Quantity, lot.UnitCost, "receipt", reference));
        return lot;
    }

    public double OnHand(string sku, DateOnly? asOf = null) =>
        _lots.Where(l => l.Sku == sku && (asOf is null || l.Expiry is null || l.Expiry >= asOf)).Sum(l => l.Remaining);

    /// <summary>Value of stock at lot cost.</summary>
    public double Value(string? sku = null) => _lots.Where(l => sku is null || l.Sku == sku).Sum(l => l.Remaining * l.UnitCost);

    /// <summary>Issues a quantity, first-expiring-first-out; expired lots are skipped. Returns the lots used.</summary>
    public IReadOnlyList<(StockLot Lot, double Quantity)> Issue(string sku, double quantity, DateOnly date, string reference = "")
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive.");
        double available = OnHand(sku, date);
        if (available + 1e-9 < quantity) throw new InvalidOperationException($"Not enough {sku}: {available:0.##} on hand, {quantity:0.##} requested.");
        var used = new List<(StockLot, double)>();
        foreach (var lot in _lots.Where(l => l.Sku == sku && l.Remaining > 0 && (l.Expiry is null || l.Expiry >= date))
                     .OrderBy(l => l.Expiry ?? DateOnly.MaxValue).ThenBy(l => l.Received))
        {
            double take = Math.Min(lot.Remaining, quantity);
            lot.Remaining -= take; quantity -= take;
            used.Add((lot, take));
            _movements.Add(new StockMovement(date, sku, lot.LotCode, -take, lot.UnitCost, "issue", reference));
            if (quantity <= 1e-9) break;
        }
        return used;
    }

    /// <summary>Issues feed to a culture cycle and logs it there per lot (feed id = lot code, price = lot cost).</summary>
    public IReadOnlyList<(StockLot Lot, double Quantity)> IssueFeed(string sku, double kg, DateOnly date, CultureCycle cycle)
    {
        var used = Issue(sku, kg, date, $"cycle:{cycle.Id}");
        foreach (var (lot, q) in used) cycle.LogFeed(date, q, lot.LotCode, lot.UnitCost);
        return used;
    }

    /// <summary>Writes off expired stock at <paramref name="date"/> and returns its value.</summary>
    public double WriteOffExpired(DateOnly date)
    {
        double value = 0;
        foreach (var lot in _lots.Where(l => l.Remaining > 0 && l.Expiry is { } e && e < date))
        {
            value += lot.Remaining * lot.UnitCost;
            _movements.Add(new StockMovement(date, lot.Sku, lot.LotCode, -lot.Remaining, lot.UnitCost, "write-off", "expired"));
            lot.Remaining = 0;
        }
        return value;
    }

    /// <summary>Average daily issue over the last <paramref name="days"/> days.</summary>
    public double DailyUse(string sku, DateOnly today, int days = 14) =>
        -_movements.Where(m => m.Sku == sku && m.Kind == "issue" && m.Date > today.AddDays(-days) && m.Date <= today).Sum(m => m.Quantity) / days;

    /// <summary>
    /// Reorder point = daily use × (lead time + safety days). <paramref name="plannedDailyUse"/> overrides the history (for example the
    /// feeding plan of the coming weeks, which grows as the animals grow).
    /// </summary>
    public ReorderAdvice Reorder(string sku, DateOnly today, double? plannedDailyUse = null, int coverDays = 30)
    {
        var item = Item(sku);
        double use = plannedDailyUse ?? DailyUse(sku, today);
        double onHand = OnHand(sku, today);
        double rop = use * (item.LeadTimeDays + item.SafetyDays);
        double order = Math.Max(0, use * (coverDays + item.LeadTimeDays + item.SafetyDays) - onHand);
        int cover = use > 0 ? (int)Math.Floor(onHand / use) : int.MaxValue;
        return new ReorderAdvice(item, Math.Round(onHand, 2), Math.Round(use, 2), Math.Round(rop, 1), Math.Round(order, 0), cover, onHand <= rop);
    }
}

// ─────────────────────────────── purchasing ───────────────────────────────

public enum PoStatus { Draft, Ordered, PartiallyReceived, Received, Cancelled }

public sealed record PoLine(string Sku, double Quantity, double UnitPrice)
{
    public double Received { get; set; }
    public double Amount => Quantity * UnitPrice;
}

/// <summary>A purchase order; receiving it creates inventory lots.</summary>
public sealed class PurchaseOrder
{
    public required string Number { get; init; }
    public required string Supplier { get; init; }
    public required DateOnly Date { get; init; }
    public List<PoLine> Lines { get; } = [];
    public PoStatus Status { get; private set; } = PoStatus.Draft;
    /// <summary>VAT (PPN) rate added to the total; 0.11 since April 2022.</summary>
    public double VatRate { get; init; } = 0.11;

    public double Subtotal => Lines.Sum(l => l.Amount);
    public double Total => Math.Round(Subtotal * (1 + VatRate), 2);

    public PurchaseOrder Place()
    {
        if (Status != PoStatus.Draft || Lines.Count == 0) throw new InvalidOperationException("Only a draft with lines can be ordered.");
        Status = PoStatus.Ordered; return this;
    }

    public void Cancel() { if (Status is PoStatus.Received) throw new InvalidOperationException("Already received."); Status = PoStatus.Cancelled; }

    /// <summary>Receives (part of) a line into inventory as a lot.</summary>
    public StockLot Receive(Inventory inventory, string sku, double quantity, string lotCode, DateOnly date, DateOnly? expiry = null)
    {
        if (Status is not (PoStatus.Ordered or PoStatus.PartiallyReceived)) throw new InvalidOperationException($"PO {Number} is {Status}.");
        var line = Lines.FirstOrDefault(l => l.Sku == sku) ?? throw new ArgumentException($"PO {Number} has no line for {sku}.");
        if (line.Received + quantity > line.Quantity * 1.05) throw new InvalidOperationException($"Receiving {quantity} would exceed the ordered {line.Quantity} by more than 5 %.");
        line.Received += quantity;
        Status = Lines.All(l => l.Received >= l.Quantity - 1e-9) ? PoStatus.Received : PoStatus.PartiallyReceived;
        return inventory.Receive(new StockLot(sku, lotCode, date, quantity, line.UnitPrice, expiry, Supplier), Number);
    }
}

/// <summary>Supplier comparison for an item from past purchase orders.</summary>
public static class Purchasing
{
    public static IReadOnlyList<(string Supplier, double MeanPrice, int Orders, double OnTimeShare)> CompareSuppliers(
        IEnumerable<PurchaseOrder> orders, string sku, Func<PurchaseOrder, bool>? deliveredOnTime = null) =>
        orders.Where(o => o.Status != PoStatus.Cancelled && o.Lines.Any(l => l.Sku == sku)).GroupBy(o => o.Supplier)
            .Select(g => (g.Key, Math.Round(g.SelectMany(o => o.Lines.Where(l => l.Sku == sku)).Average(l => l.UnitPrice), 2), g.Count(),
                Math.Round(deliveredOnTime is null ? double.NaN : g.Count(deliveredOnTime) / (double)g.Count(), 2)))
            .OrderBy(x => x.Item2).ToList();
}

// ─────────────────────────────── payroll ───────────────────────────────

public enum PayBasis { Monthly, Daily }

public sealed record Employee(string Id, string Name, string Role, PayBasis Basis, double Rate, DateOnly Joined)
{
    /// <summary>Harvest bonus per kg harvested in the period (shared by pond staff), IDR.</summary>
    public double HarvestBonusPerKg { get; init; }
    public bool ParticipatesInBpjs { get; init; } = true;
}

/// <summary>
/// Indonesian statutory payroll parameters (BPJS Ketenagakerjaan and Kesehatan, overtime rules of Kepmenakertrans 102/2004). Rates and
/// wage caps change by regulation — update them each year. Income tax (PPh 21, TER method) is not computed.
/// </summary>
public sealed record PayrollRules
{
    public double JhtEmployer { get; init; } = 0.037;
    public double JhtEmployee { get; init; } = 0.02;
    /// <summary>Work-accident insurance, 0.24–1.74 % by risk class.</summary>
    public double Jkk { get; init; } = 0.0054;
    public double Jkm { get; init; } = 0.003;
    public double JpEmployer { get; init; } = 0.02;
    public double JpEmployee { get; init; } = 0.01;
    /// <summary>Wage cap for the pension (JP) contribution — revised every year.</summary>
    public double JpWageCap { get; init; } = 10_042_300;
    public double HealthEmployer { get; init; } = 0.04;
    public double HealthEmployee { get; init; } = 0.01;
    public double HealthWageCap { get; init; } = 12_000_000;
    /// <summary>Monthly hours divisor for the hourly overtime base (1/173 of the monthly wage).</summary>
    public double HoursPerMonth { get; init; } = 173;
    /// <summary>Working days per month used to convert daily wages for contributions.</summary>
    public double DaysPerMonth { get; init; } = 25;
}

public sealed record Timesheet(string EmployeeId, int DaysWorked, IReadOnlyList<double> OvertimeHoursPerDay);

public sealed record Payslip(string EmployeeId, string Name, double BaseWage, double Overtime, double HarvestBonus, double Thr, double Gross,
    double EmployeeDeductions, double NetPay, double EmployerContributions, double EmployerCost);

public static class Payroll
{
    /// <summary>Overtime pay on a working day: first hour 1.5×, each further hour 2× the hourly wage (1/173 of the monthly wage).</summary>
    public static double OvertimePay(double monthlyWage, IEnumerable<double> hoursPerDay, PayrollRules? rules = null)
    {
        var r = rules ?? new PayrollRules();
        double hourly = monthlyWage / r.HoursPerMonth;
        return hoursPerDay.Sum(h => h <= 0 ? 0 : (Math.Min(h, 1) * 1.5 + Math.Max(0, h - 1) * 2) * hourly);
    }

    /// <summary>Religious-holiday allowance (THR): one month's wage after 12 months of service, prorated (months / 12) from one month.</summary>
    public static double Thr(double monthlyWage, DateOnly joined, DateOnly holiday)
    {
        int months = (holiday.Year - joined.Year) * 12 + holiday.Month - joined.Month - (holiday.Day < joined.Day ? 1 : 0);
        return months < 1 ? 0 : months >= 12 ? monthlyWage : monthlyWage * months / 12.0;
    }

    /// <summary>A month's payslip. <paramref name="harvestKg"/> is the farm's harvest in the period for the per-kg bonus.</summary>
    public static Payslip Monthly(Employee e, Timesheet t, double harvestKg = 0, DateOnly? thrHoliday = null, PayrollRules? rules = null)
    {
        var r = rules ?? new PayrollRules();
        double monthly = e.Basis == PayBasis.Monthly ? e.Rate : e.Rate * r.DaysPerMonth;
        double basePay = e.Basis == PayBasis.Monthly ? e.Rate : e.Rate * t.DaysWorked;
        double ot = OvertimePay(monthly, t.OvertimeHoursPerDay, r);
        double bonus = e.HarvestBonusPerKg * harvestKg;
        double thr = thrHoliday is { } h ? Thr(monthly, e.Joined, h) : 0;
        double gross = basePay + ot + bonus + thr;
        double ee = 0, er = 0;
        if (e.ParticipatesInBpjs)
        {
            double jpBase = Math.Min(monthly, r.JpWageCap), healthBase = Math.Min(monthly, r.HealthWageCap);
            ee = monthly * r.JhtEmployee + jpBase * r.JpEmployee + healthBase * r.HealthEmployee;
            er = monthly * (r.JhtEmployer + r.Jkk + r.Jkm) + jpBase * r.JpEmployer + healthBase * r.HealthEmployer;
        }
        return new Payslip(e.Id, e.Name, Math.Round(basePay), Math.Round(ot), Math.Round(bonus), Math.Round(thr), Math.Round(gross),
            Math.Round(ee), Math.Round(gross - ee), Math.Round(er), Math.Round(gross + er));
    }

    /// <summary>Labour cost per kg harvested for a period (employer cost of all payslips over the harvest).</summary>
    public static double LabourCostPerKg(IEnumerable<Payslip> slips, double harvestKg) => harvestKg <= 0 ? double.NaN : slips.Sum(s => s.EmployerCost) / harvestKg;
}

public static class OperationsBuilderExtensions
{
    public static AquaBuilder AddOperations(this AquaBuilder b) => b.TryAddModule("Operations", _ => { });
}
