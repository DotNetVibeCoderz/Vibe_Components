namespace GeoPandas.Net.Tests;

public class DataFrameTests
{
    private static DataFrame Sample() => new(
        Column.Create("city", new[] { "A", "B", "C", "D", "E" }),
        Column.Create("region", new[] { "x", "y", "x", "y", "x" }),
        Column.Create("pop", new[] { 10.0, 20.0, 30.0, 40.0, 50.0 }),
        Column<long>.FromNullable("code", new long?[] { 1, null, 3, 4, 5 }));

    [Fact]
    public void Shape_Filter_Sort_Head()
    {
        var df = Sample();
        Assert.Equal((5, 4), df.Shape);
        var big = df.Filter<double>("pop", v => v > 25);
        Assert.Equal(3, big.RowCount);
        var sorted = df.SortBy("pop", ascending: false).Head(2);
        Assert.Equal(new object?[] { "E", "D" }, sorted["city"].AsObjects().ToArray());
    }

    [Fact]
    public void Nulls_AreTracked()
    {
        var df = Sample();
        Assert.Equal(1, df["code"].NullCount);
        Assert.True(df["code"].IsNull(1));
        var sorted = df.SortBy("code");
        Assert.True(sorted["code"].IsNull(4)); // nulls last
    }

    [Fact]
    public void GroupBy_Aggregates()
    {
        var g = Sample().GroupBy("region").Agg(("pop", AggFunc.Sum, "pop_sum"), ("pop", AggFunc.Mean, "pop_mean"), ("city", AggFunc.Count, "n"));
        Assert.Equal(2, g.RowCount);
        Assert.Equal(90.0, g["pop_sum"].GetDouble(0));
        Assert.Equal(30.0, g["pop_mean"].GetDouble(0));
        Assert.Equal(3L, g["n"].GetValue(0));
    }

    [Fact]
    public void Merge_InnerAndLeft()
    {
        var right = new DataFrame(Column.Create("city", new[] { "A", "C", "Z" }), Column.Create("mayor", new[] { "m1", "m3", "mz" }));
        var inner = Sample().Merge(right, "city");
        Assert.Equal(2, inner.RowCount);
        var left = Sample().Merge(right, "city", JoinHow.Left);
        Assert.Equal(5, left.RowCount);
        Assert.Null(left["mayor"].GetValue(1));
        var outer = Sample().Merge(right, "city", JoinHow.Outer);
        Assert.Equal(6, outer.RowCount);
        Assert.Equal("Z", outer["city"].GetValue(5));
    }

    [Fact]
    public void Describe_And_ValueCounts()
    {
        var d = Sample().Describe();
        Assert.Equal(30.0, d["pop"].GetDouble(1)); // mean
        Assert.Equal(30.0, d["pop"].GetDouble(5)); // median
        var vc = Sample().ValueCounts("region");
        Assert.Equal("x", vc["region"].GetValue(0));
        Assert.Equal(3L, vc["count"].GetValue(0));
    }

    [Fact]
    public void Concat_FillsMissingColumns()
    {
        var a = new DataFrame(Column.Create("k", new[] { 1L, 2L }));
        var b = new DataFrame(Column.Create("k", new[] { 3L }), Column.Create("v", new[] { "z" }));
        var c = DataFrame.Concat(a, b);
        Assert.Equal(3, c.RowCount);
        Assert.Null(c["v"].GetValue(0));
        Assert.Equal("z", c["v"].GetValue(2));
    }

    [Fact]
    public void WithColumn_Computed_And_Immutability()
    {
        var df = Sample();
        var df2 = df.WithColumn<double>("pop2", r => r.GetDouble("pop") * 2);
        Assert.Equal(4, df.ColumnCount);
        Assert.Equal(5, df2.ColumnCount);
        Assert.Equal(100.0, df2["pop2"].GetDouble(4));
    }

    [Fact]
    public void ToString_PrintsTable()
    {
        var s = Sample().ToString();
        Assert.Contains("city", s);
        Assert.Contains("null", s);
    }
}
