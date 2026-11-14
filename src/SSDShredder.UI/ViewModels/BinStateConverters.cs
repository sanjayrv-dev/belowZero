using Avalonia.Data.Converters;

namespace SSDShredder.UI.ViewModels;

/// <summary>Small IValueConverters for BinWindow.axaml visibility bindings.</summary>
public static class BinStateConverters
{
    public static readonly IValueConverter IsNotNull =
        new FuncValueConverter<object?, bool>(value => value is not null);

    public static readonly IValueConverter IsIdleOrTerminal =
        new FuncValueConverter<BinState, bool>(state => state is BinState.Idle or BinState.Completed or BinState.Attention);

    public static readonly IValueConverter IsAwaitingConfirmation =
        new FuncValueConverter<BinState, bool>(state => state == BinState.AwaitingConfirmation);

    public static readonly IValueConverter IsProcessing =
        new FuncValueConverter<BinState, bool>(state => state == BinState.Processing);

    public static readonly IValueConverter IsCompletedOrAttention =
        new FuncValueConverter<BinState, bool>(state => state is BinState.Completed or BinState.Attention);
}
