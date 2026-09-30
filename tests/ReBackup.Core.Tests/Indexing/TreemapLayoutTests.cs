using FluentAssertions;
using ReBackup.Core.Indexing;

namespace ReBackup.Core.Tests.Indexing;

public class TreemapLayoutTests
{
    private const double Tolerance = 1e-9;

    private static IReadOnlyList<TreemapTile<double>> Layout(TreemapRect bounds, params double[] weights) =>
        TreemapLayout.Squarify(weights, w => w, bounds);

    [Fact]
    public void Single_item_fills_the_bounds()
    {
        var bounds = new TreemapRect(10, 20, 300, 200);

        Layout(bounds, 5).Should().ContainSingle().Which.Rect.Should().Be(bounds);
    }

    [Fact]
    public void Two_equal_items_split_a_wide_rectangle_into_squares()
    {
        var tiles = Layout(new TreemapRect(0, 0, 100, 50), 1, 1);

        tiles.Select(t => t.Rect).Should().BeEquivalentTo(
            [new TreemapRect(0, 0, 50, 50), new TreemapRect(50, 0, 50, 50)]);
    }

    [Fact]
    public void Classic_example_places_the_first_row_as_a_column_on_the_left()
    {
        // Bruls, Huizing, van Wijk: weights 6,6,4,3,2,2,1 in a 6x4 rectangle.
        var tiles = Layout(new TreemapRect(0, 0, 6, 4), 6, 6, 4, 3, 2, 2, 1);

        tiles.Should().HaveCount(7);
        AssertRect(tiles[0].Rect, 0, 0, 3, 2);
        AssertRect(tiles[1].Rect, 0, 2, 3, 2);
        tiles.Select(t => t.Item).Should().Equal(6, 6, 4, 3, 2, 2, 1);
    }

    [Fact]
    public void Areas_are_proportional_to_weights_and_fill_the_bounds()
    {
        var bounds = new TreemapRect(5, 7, 640, 360);
        var weights = new double[] { 50, 3, 120, 8, 8, 1, 33, 0.5 };

        var tiles = Layout(bounds, weights);

        var scale = bounds.Area / weights.Sum();
        foreach (var tile in tiles)
            tile.Rect.Area.Should().BeApproximately(tile.Item * scale, 1e-6);
        tiles.Sum(t => t.Rect.Area).Should().BeApproximately(bounds.Area, 1e-6);
    }

    [Fact]
    public void Tiles_stay_inside_the_bounds()
    {
        var bounds = new TreemapRect(5, 7, 640, 360);

        var tiles = Layout(bounds, Enumerable.Range(1, 200).Select(i => (double)(i * i % 97 + 1)).ToArray());

        foreach (var rect in tiles.Select(t => t.Rect))
        {
            rect.X.Should().BeGreaterThanOrEqualTo(bounds.X - Tolerance);
            rect.Y.Should().BeGreaterThanOrEqualTo(bounds.Y - Tolerance);
            (rect.X + rect.Width).Should().BeLessThanOrEqualTo(bounds.X + bounds.Width + 1e-6);
            (rect.Y + rect.Height).Should().BeLessThanOrEqualTo(bounds.Y + bounds.Height + 1e-6);
            rect.Width.Should().BeGreaterThanOrEqualTo(0);
            rect.Height.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public void Items_without_weight_are_left_out()
    {
        Layout(new TreemapRect(0, 0, 10, 10), 0, 4, -1).Should().ContainSingle().Which.Item.Should().Be(4);
    }

    [Fact]
    public void Nothing_to_lay_out_yields_an_empty_result()
    {
        Layout(new TreemapRect(0, 0, 10, 10)).Should().BeEmpty();
        Layout(new TreemapRect(0, 0, 10, 10), 0, 0).Should().BeEmpty();
        Layout(new TreemapRect(0, 0, 0, 10), 1, 2).Should().BeEmpty();
    }

    private static void AssertRect(TreemapRect rect, double x, double y, double width, double height)
    {
        rect.X.Should().BeApproximately(x, Tolerance);
        rect.Y.Should().BeApproximately(y, Tolerance);
        rect.Width.Should().BeApproximately(width, Tolerance);
        rect.Height.Should().BeApproximately(height, Tolerance);
    }
}
