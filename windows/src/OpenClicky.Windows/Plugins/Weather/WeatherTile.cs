//
//  WeatherTile.cs
//  OpenClicky (Windows port of Plugins/Weather/WeatherTile.swift)
//
//  The weather tile on home: the city, the temperature, the sky as a symbol and a word, today's high
//  and low, all on a gradient of the sky it describes. A click does what the tile is asking for:
//  switch between Celsius and Fahrenheit when it shows the weather, ask for the location when it
//  needs it, try again after a failure. The symbols are Segoe UI Symbol's (drawn in white: WPF has
//  no colour glyphs, where the Mac's SF Symbols are multicolour).
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Weather;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Plugins.Weather;

internal sealed class WeatherTile : Border, IIslandContent
{
    /// <summary>The app's one store (the Mac's WeatherStore.shared).</summary>
    public static WeatherStore Store { get; } = new(Preferences.Shared, new WindowsWeatherLocation());

    private const string LocationGlyph = "";
    private static readonly SolidColorBrush DimText = DS.Brush(Colors.White, 0.7);

    private readonly Border tile;
    private WeatherTileState? shownState;
    private WeatherUnit shownUnit;

    public WeatherTile()
    {
        tile = IslandDashUi.Tile(new Grid(), padding: 12);
        tile.Cursor = Cursors.Hand;
        Child = tile;
        // The island must not take the click on the tile as a click on itself.
        tile.MouseLeftButtonDown += (_, eventArgs) => eventArgs.Handled = true;
        tile.MouseLeftButtonUp += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            HandleClick();
        };
        Loaded += (_, _) =>
        {
            Store.Changed += OnStoreChanged;
            Store.BeginWatching();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Store.Changed -= OnStoreChanged;
            Store.EndWatching();
        };
        Refresh();
    }

    private void HandleClick()
    {
        switch (Store.State)
        {
            case WeatherTileState.Ready:
                Store.ToggleUnit();
                break;
            case WeatherTileState.NeedsLocationPermission:
                _ = Store.RequestLocationPermissionAsync();
                break;
            case WeatherTileState.Failed:
                Store.RetryNow();
                break;
        }
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        var state = Store.State;
        if (state == shownState && Store.Unit == shownUnit) return;
        shownState = state;
        shownUnit = Store.Unit;
        if (state is WeatherTileState.Ready ready)
        {
            var sky = ready.Report.Condition.Sky;
            tile.Background = new LinearGradientBrush(Color(sky.Top()), Color(sky.Bottom()), 90);
            tile.Child = ReadyContent(ready);
        }
        else
        {
            tile.Background = IslandDashUi.TileFillBrush;
            tile.Child = Placeholder(state switch
            {
                WeatherTileState.NeedsLocationPermission { CanAsk: true } => "Click to use your location",
                WeatherTileState.NeedsLocationPermission => "Location is off in Windows Settings",
                WeatherTileState.NeedsPlace => "Add a place in Settings",
                WeatherTileState.Failed failed => failed.Message,
                _ => "Loading weather…",
            });
        }
        var hint = state switch
        {
            WeatherTileState.Ready => "switches between Celsius and Fahrenheit",
            WeatherTileState.NeedsLocationPermission => "asks to use your location",
            WeatherTileState.Failed => "tries again",
            _ => null,
        };
        tile.ToolTip = hint;
        tile.Cursor = hint is null ? Cursors.Arrow : Cursors.Hand;
        AutomationProperties.SetHelpText(tile, hint ?? "");
    }

    public void Tick() { }

    private FrameworkElement ReadyContent(WeatherTileState.Ready ready)
    {
        var report = ready.Report;
        var unit = Store.Unit;
        var grid = new Grid();
        for (var row = 0; row < 5; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = row == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }

        var cityRow = new StackPanel { Orientation = Orientation.Horizontal };
        var city = Text(ready.CityName, 12, DS.HUD.TextBrush, FontWeights.SemiBold, trimming: TextTrimming.CharacterEllipsis);
        city.MaxWidth = ready.IsAutomaticLocation ? 80 : 92;
        cityRow.Children.Add(city);
        if (ready.IsAutomaticLocation)
        {
            var arrow = Glyph(LocationGlyph, 8, DS.HUD.TextBrush);
            arrow.Margin = new Thickness(3, 1, 0, 0);
            cityRow.Children.Add(arrow);
        }
        grid.Children.Add(cityRow);

        var temperature = new StackPanel { Orientation = Orientation.Horizontal };
        var degrees = Text($"{unit.WholeDegrees(report.TemperatureCelsius)}°", 32, DS.HUD.TextBrush);
        degrees.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        degrees.LineHeight = 38;
        temperature.Children.Add(degrees);
        var letter = Text(unit.Letter(), 13, DS.HUD.TextBrush, FontWeights.SemiBold);
        letter.VerticalAlignment = VerticalAlignment.Top;
        letter.Margin = new Thickness(1, 6, 0, 0);
        temperature.Children.Add(letter);
        Grid.SetRow(temperature, 1);
        grid.Children.Add(temperature);

        var condition = report.Condition;
        var sky = new StackPanel { Orientation = Orientation.Horizontal };
        var symbol = Text(SymbolText(condition.Symbol), 13, DS.HUD.TextBrush, family: IslandDashUi.SymbolFont);
        symbol.Margin = new Thickness(0, 0, 4, 0);
        sky.Children.Add(symbol);
        sky.Children.Add(Text(condition.Word, 11, DS.HUD.TextBrush, FontWeights.SemiBold, trimming: TextTrimming.CharacterEllipsis));
        Grid.SetRow(sky, 3);
        grid.Children.Add(sky);

        if (report.HighCelsius is { } high && report.LowCelsius is { } low)
        {
            var highLow = Text($"H:{unit.WholeDegrees(high)}° L:{unit.WholeDegrees(low)}°", 11, DimText, FontWeights.SemiBold);
            highLow.Margin = new Thickness(0, 2, 0, 0);
            Grid.SetRow(highLow, 4);
            grid.Children.Add(highLow);
        }
        AutomationProperties.SetName(grid, $"{ready.CityName}, {unit.WholeDegrees(report.TemperatureCelsius)} degrees {unit.Title()}, {condition.Word}");
        return grid;
    }

    private static FrameworkElement Placeholder(string line)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var symbol = Text(SymbolText(WeatherSymbol.CloudSun), 18, DimText, family: IslandDashUi.SymbolFont);
        symbol.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(symbol);
        var words = Text(line, 11, DimText, FontWeights.Medium, wrapping: TextWrapping.Wrap, trimming: TextTrimming.CharacterEllipsis);
        words.MaxHeight = 46;
        Grid.SetRow(words, 2);
        grid.Children.Add(words);
        AutomationProperties.SetName(grid, line);
        return grid;
    }

    /// <summary>The sky in Segoe UI Symbol.</summary>
    internal static string SymbolText(WeatherSymbol symbol) => symbol switch
    {
        WeatherSymbol.Sun => "☀",
        WeatherSymbol.Moon => "☾",
        WeatherSymbol.CloudSun => "⛅",
        WeatherSymbol.CloudMoon => "☁",
        WeatherSymbol.Fog => char.ConvertFromUtf32(0x1F32B),
        WeatherSymbol.Drizzle or WeatherSymbol.Rain => char.ConvertFromUtf32(0x1F327),
        WeatherSymbol.Snow => char.ConvertFromUtf32(0x1F328),
        WeatherSymbol.Thunderstorm => "⛈",
        _ => "☁",
    };

    private static Color Color(WeatherRgb rgb) => System.Windows.Media.Color.FromRgb(rgb.Red, rgb.Green, rgb.Blue);
}
