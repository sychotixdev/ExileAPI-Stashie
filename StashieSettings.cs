using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Stashie
{
    public class StashieSettings : ISettings
    {
        public List<string> AllStashNames = new();
        public readonly Dictionary<string, ListIndexNode> CustomFilterOptions = new();
        
        [Menu("Filter File")]
        public ListNode FilterFile { get; set; } = new ListNode();

        [Menu("Stash Hotkey")]
        public HotkeyNode DropHotkey { get; set; } = Keys.F3;

        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        public int[,] IgnoredCells { get; set; } =
        {
            {1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            {1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
            {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        };

    }
}