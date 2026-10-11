using Game.UI.Tooltip;
using SeenBetterDays.Data;

namespace SeenBetterDays.Systems
{
    /// <summary>One row of the maintenance tooltip: drawn by the game as a tooltip of its own,
    /// with the mod's icon.</summary>
    public struct MaintenanceRow
    {
        public readonly string Icon;
        public readonly string Text;
        public readonly TooltipColor Color;

        public MaintenanceRow(string icon, string text, TooltipColor color = TooltipColor.Info)
        {
            Icon = icon;
            Text = text;
            Color = color;
        }
    }

    /// <summary>
    /// The tooltip's icons, shipped with the mod (UI/icons, deployed as sbd-icons/). The game
    /// serves the mod's folder under coui://ui-mods/; the folder name is the mod's own so it does
    /// not meet another mod's files there.
    /// </summary>
    public static class TooltipIcons
    {
        private const string Root = "coui://ui-mods/sbd-icons/";

        public const string Author = Root + "author.svg";
        public const string Garbage = Root + "garbage.svg";
        public const string GarbageWarning = Root + "garbage-warning.svg";
        public const string Colour = Root + "colour.svg";
        public const string Decals = Root + "decals.svg";
        public const string Cause = Root + "cause.svg";

        public static string State(VisualState state)
        {
            switch (state)
            {
                case VisualState.Maintained: return Root + "state-maintained.svg";
                case VisualState.Aged: return Root + "state-aged.svg";
                case VisualState.Worn: return Root + "state-worn.svg";
                case VisualState.Neglected: return Root + "state-neglected.svg";
                default: return Root + "state-decayed.svg";
            }
        }
    }
}
