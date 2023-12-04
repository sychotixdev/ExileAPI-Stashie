using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ImGuiNET;
using InputHumanizer.Input;
using ItemFilterLibrary;
using SharpDX;
using static ExileCore.PoEMemory.MemoryObjects.ServerInventory;
using Vector2N = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Stashie
{
    public class StashieCore : BaseSettingsPlugin<StashieSettings>
    {
        private Vector2 _clickWindowOffset;

        private List<CustomFilter> _currentFilter;
        private List<FilterResult> _itemsToStash;

        private List<ListIndexNode> _settingsListNodes;
        private Action _filterTabs;
        private string[] _stashTabNamesByIndex;

        private SyncTask<bool> _currentOperation;

        private IInputController _inputController;

        public StashieCore()
        {
            Name = "Stashie";
        }

        public override bool Initialise()
        {
            Setup();

            Input.RegisterKey(Settings.DropHotkey);

            Settings.DropHotkey.OnValueChanged += () => { Input.RegisterKey(Settings.DropHotkey); };

            Settings.FilterFile.OnValueSelected = _ => LoadCustomFilters();

            return true;
        }

        private void Setup()
        {
            _settingsListNodes = new List<ListIndexNode>(100);

            LoadCustomFilters();

            try
            {
                var stashNames = GameController.Game.IngameState.IngameUi.StashElement.AllStashNames;
                UpdateStashNames(stashNames);
            }
            catch (Exception e)
            {
                LogError($"{Name}: Failed to get Stash Names: {e}");
            }
        }

        public override Job Tick()
        {
            return null;
        }

        public override void Render()
        {
            if (!IsInGame(GameController) || !IsInventoryVisible() || !IsStashVisible()) return;

            if (_currentOperation != null)
            {
                DebugWindow.LogMsg($"{Name}: Stashing Items.");
                TaskUtils.RunOrRestart(ref _currentOperation, () => null);
                return;
            }

            if (Input.IsKeyDown(Settings.DropHotkey.Value))
            {
                _currentOperation = StashItems();
            }
        }

        private async SyncTask<bool> StashUpdater()
        {
            await TaskUtils.CheckEveryFrame(() => IsInGame(GameController), new CancellationTokenSource(5000).Token);

            if (!IsInGame(GameController))
            {
                return false;
            }

            StashElement stashPanel = GameController.Game.IngameState?.IngameUi?.StashElement;

            await TaskUtils.CheckEveryFrame(() => stashPanel is not null, new CancellationTokenSource(5000).Token);

            if (stashPanel is null)
            {
                return false;
            }

            IList<string> cachedNames = Settings.AllStashNames;
            IList<string> realNames = stashPanel!.AllStashNames;

            if (realNames.Count + 1 != cachedNames.Count)
            {
                UpdateStashNames(realNames);
                return true;
            }

            for (int i = 0; i < realNames.Count; i++)
            {
                string cachedName = cachedNames[i + 1];
                if (cachedName.Equals(realNames[i])) continue;

                UpdateStashNames(realNames);
                break;
            }

            return true;
        }

        private async SyncTask<bool> StashItems()
        {
            int initialStashTab = GetIndexOfCurrentVisibleTab();

            if (initialStashTab < 0)
            {
                DebugWindow.LogError($"{Name}: Invalid Stash Tab.");
                return false;
            }

            if (await ParseItems() == false) return false;

            if (_itemsToStash.Count == 0)
            {
                DebugWindow.LogMsg($"{Name}: No Items to Stash.", 5);
                return false;
            }

            // Now that we're all validated up, grab an input lock so we can get going
            var tryGetInputController = GameController.PluginBridge.GetMethod<Func<string, IInputController>>("InputHumanizer.TryGetInputController");
            if (tryGetInputController == null)
            {
                LogError("InputHumanizer method not registered.");
                return false;
            }

            if ((_inputController = tryGetInputController(this.Name)) != null)
            {
                using (_inputController)
                {


                    var itemsSortedByStash = _itemsToStash
                .OrderBy(x => x.SkipSwitchTab || x.StashIndex == initialStashTab ? 0 : 1)
                .ThenBy(x => x.StashIndex)
                .ToList();

                    _inputController.KeyDown(Keys.LControlKey);

                    foreach (FilterResult filterResult in itemsSortedByStash)
                    {
                        if (!filterResult.SkipSwitchTab)
                        {
                            if (await SwitchTab(filterResult.StashIndex) == false) continue;
                        }

                        int visibleStashIndex = GetIndexOfCurrentVisibleTab();
                        InventoryType visibleInventoryType = GetTypeOfCurrentVisibleStash();

                        await TaskUtils.CheckEveryFrame(() =>
                                GameController.IngameState.IngameUi.StashElement
                                    .AllInventories[visibleStashIndex] is not null &&
                                visibleInventoryType != InventoryType.InvalidInventory,
                            new CancellationTokenSource(5000).Token);

                        if (GameController.IngameState.IngameUi.StashElement.AllInventories[visibleStashIndex] is null ||
                            visibleInventoryType == InventoryType.InvalidInventory)
                        {
                            DebugWindow.LogError($"{Name}: Stash Tab Error. Index: {visibleStashIndex}.");
                            return false;
                        }

                        await StashItem(filterResult);
                    }

                    await _inputController.KeyUp(Keys.LControlKey);

                }
            }
            return true;
        }

        private async SyncTask<bool> StashItem(FilterResult filterResult)
        {
            await _inputController.MoveMouse(new Vector2N(
                    filterResult.ClickPosition.X + _clickWindowOffset.X,
                    filterResult.ClickPosition.Y + _clickWindowOffset.Y
                ));

            await Task.Delay(_inputController.GenerateDelay());

            bool isShiftUsed = false;

            if (filterResult.ShiftForStashing)
            {
                _inputController.KeyDown(Keys.ShiftKey);
                isShiftUsed = true;
            }

            await _inputController.Click(MouseButtons.Left);

            if (isShiftUsed)
            {
                await _inputController.KeyUp(Keys.ShiftKey);
            }
            
            await Task.Delay(_inputController.GenerateDelay());

            return true;
        }

        private async SyncTask<bool> ParseItems()
        {
            ServerInventory inventory = GameController.Game.IngameState.Data.ServerData.PlayerInventories[0].Inventory;
            IList<InventSlotItem> inventoryItems = inventory.InventorySlotItems;

            await TaskUtils.CheckEveryFrame(() => inventoryItems != null, new CancellationTokenSource(5000).Token);

            if (inventoryItems == null)
            {
                DebugWindow.LogError($"{Name}: Unable to get Inventory Items.");
                return false;
            }

            IList<InventSlotItem> sortedInventoryItems = inventoryItems
                .OrderBy(item => item.PosX)
                .ThenBy(item => item.PosY)
                .ToList();

            _itemsToStash = new List<FilterResult>();
            _clickWindowOffset = GameController.Window.GetWindowRectangle().TopLeft;

            foreach (InventSlotItem inventoryItem in sortedInventoryItems)
            {
                if (inventoryItem.Item is null || inventoryItem is { Address: 0 }) continue;
                if (CheckIgnoreCells(inventoryItem)) continue;

                ItemData itemData = new ItemData(inventoryItem.Item, GameController);
                FilterResult filterResult = CheckFilters(itemData, CalculateClickPosition(inventoryItem));

                if (filterResult is not null) _itemsToStash.Add(filterResult);
            }

            return true;
        }

        private async SyncTask<bool> SwitchTab(int tabIndex)
        {
            int currentVisibleTab = GetIndexOfCurrentVisibleTab();
            int travelDistance = tabIndex - currentVisibleTab;

            if (travelDistance == 0) return true;

            bool isToTheLeft = travelDistance < 0;

            travelDistance = Math.Abs(travelDistance);

            if (isToTheLeft)
            {
                await PressKey(Keys.Left, travelDistance);
            }
            else
            {
                await PressKey(Keys.Right, travelDistance);
            }

            await TaskUtils.CheckEveryFrame(() => GetIndexOfCurrentVisibleTab() == tabIndex,
                new CancellationTokenSource(5000).Token);

            if (GetIndexOfCurrentVisibleTab() != tabIndex)
            {
                DebugWindow.LogError($"{Name}: Failed to switch to Stash Tab {tabIndex}.");
                return false;
            }

            await Task.Delay(Random.Shared.Next(80, 150));

            return true;
        }

        private async SyncTask<bool> PressKey(Keys key, int keyPresses = 1)
        {
            for (var i = 0; i < keyPresses; i++)
            {
                _inputController.KeyDown(key);
                await _inputController.KeyUp(key);
            }

            return true;
        }

        private static bool IsInGame(GameController gameController)
        {
            return gameController?.Game?.IngameState?.InGame ?? false;
        }

        private bool IsInventoryVisible()
        {
            return GameController?.IngameState?.IngameUi?.InventoryPanel?.IsVisible ?? false;
        }

        private bool IsStashVisible()
        {
            return GameController?.IngameState?.IngameUi?.StashElement?.IsVisibleLocal ?? false;
        }

        public override void AreaChange(AreaInstance area)
        {
            _currentOperation ??= StashUpdater();

            if (_currentOperation is not null)
            {
                TaskUtils.RunOrRestart(ref _currentOperation, () => null);
            }
        }

        public override void DrawSettings()
        {
            DrawReloadConfigButton();
            DrawIgnoredCellsSettings();
            base.DrawSettings();

            _filterTabs?.Invoke();
        }

        private void LoadCustomFilters()
        {
            var configFileDirectory = Path.Combine(ConfigDirectory);

            if (!Directory.Exists(configFileDirectory))
            {
                Directory.CreateDirectory(configFileDirectory);
                return;
            }

            var dirInfo = new DirectoryInfo(configFileDirectory);
            Settings.FilterFile.Values = dirInfo.GetFiles("*.ifl").Select(x => Path.GetFileNameWithoutExtension(x.Name))
                .ToList();
            if (Settings.FilterFile.Values.Any() && !Settings.FilterFile.Values.Contains(Settings.FilterFile.Value))
            {
                Settings.FilterFile.Value = Settings.FilterFile.Values.First();
            }

            if (!string.IsNullOrWhiteSpace(Settings.FilterFile.Value))
            {
                var filterFilePath = Path.Combine(configFileDirectory, $"{Settings.FilterFile.Value}.ifl");
                if (File.Exists(filterFilePath))
                {
                    _currentFilter = FilterParser.Load($"{Settings.FilterFile.Value}.ifl", filterFilePath);

                    foreach (var customFilter in _currentFilter)
                    {
                        foreach (var filter in customFilter.Filters)
                        {
                            if (!Settings.CustomFilterOptions.TryGetValue(
                                    customFilter.ParentMenuName + filter.FilterName, out var indexNodeS))
                            {
                                indexNodeS = new ListIndexNode { Value = "Ignore", Index = -1 };
                                Settings.CustomFilterOptions.Add(customFilter.ParentMenuName + filter.FilterName,
                                    indexNodeS);
                            }

                            filter.StashIndexNode = indexNodeS;
                            _settingsListNodes.Add(indexNodeS);
                        }
                    }
                }
                else
                {
                    _currentFilter = null;
                    LogError("Item Filter not found.");
                }
            }
        }

        private void SaveIgnoredSLotsFromInventoryTemplate()
        {
            Settings.IgnoredCells = new[,]
            {
                { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }
            };
            try
            {
                var inventoryServer = GameController.IngameState.Data.ServerData.PlayerInventories[0];

                foreach (var item in inventoryServer.Inventory.InventorySlotItems)
                {
                    var baseC = item.Item.GetComponent<Base>();
                    var itemSizeX = baseC.ItemCellsSizeX;
                    var itemSizeY = baseC.ItemCellsSizeY;
                    var inventPosX = item.PosX;
                    var inventPosY = item.PosY;
                    for (var y = 0; y < itemSizeY; y++)
                    for (var x = 0; x < itemSizeX; x++)
                        Settings.IgnoredCells[y + inventPosY, x + inventPosX] = 1;
                }
            }
            catch (Exception e)
            {
                LogError($"{e}", 5);
            }
        }

        private void DrawReloadConfigButton()
        {
            if (ImGui.Button("Reload Config"))
            {
                LoadCustomFilters();
                GenerateMenu();
                DebugWindow.LogMsg($"{Name}: Reloaded Stashie Config", 2, Color.LimeGreen);
            }
        }

        private void DrawIgnoredCellsSettings()
        {
            try
            {
                if (ImGui.Button("Copy Inventory")) SaveIgnoredSLotsFromInventoryTemplate();

                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        $"Checked = Ignored.");
            }
            catch (Exception e)
            {
                DebugWindow.LogError(e.ToString(), 10);
            }

            var number = 1;
            for (var i = 0; i < 5; i++)
            for (var j = 0; j < 12; j++)
            {
                var toggled = Convert.ToBoolean(Settings.IgnoredCells[i, j]);
                if (ImGui.Checkbox($"##{number}IgnoredCells", ref toggled)) Settings.IgnoredCells[i, j] ^= 1;

                if ((number - 1) % 12 < 11) ImGui.SameLine();

                number += 1;
            }
        }

        private void GenerateMenu()
        {
            _stashTabNamesByIndex = _renamedAllStashNames.ToArray();

            _filterTabs = null;

            foreach (var parent in _currentFilter)
                _filterTabs += () =>
                {
                    ImGui.TextColored(new Vector4(0f, 1f, 0.022f, 1f), parent.ParentMenuName);
                    ImGui.Separator();
                    foreach (var filter in parent.Filters)
                        if (Settings.CustomFilterOptions.TryGetValue(parent.ParentMenuName + filter.FilterName,
                                out var indexNode))
                        {
                            var formattableString =
                                $"{filter.FilterName} => {_renamedAllStashNames[indexNode.Index + 1]}##{parent.ParentMenuName + filter.FilterName}";

                            ImGui.Columns(2, formattableString, true);
                            ImGui.SetColumnWidth(0, 320);
                            ImGui.SetColumnWidth(1, 300);

                            if (ImGui.InvisibleButton(formattableString, new Vector2N(300, 20)))
                                ImGui.OpenPopup(formattableString);

                            ImGui.SameLine();
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 300);
                            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2);
                            ImGui.Text(filter.FilterName);

                            ImGui.SameLine();
                            ImGui.NextColumn();
                            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2);

                            var item = indexNode.Index + 1;
                            var filterName = filter.FilterName;

                            if (string.IsNullOrWhiteSpace(filterName))
                                filterName = "Null";

                            if (ImGui.Combo($"##{parent.ParentMenuName + filter.FilterName}", ref item,
                                    _stashTabNamesByIndex,
                                    _stashTabNamesByIndex.Length))
                            {
                                indexNode.Value = _stashTabNamesByIndex[item];
                                OnSettingsStashNameChanged(indexNode, _stashTabNamesByIndex[item]);
                            }

                            ImGui.NextColumn();
                            ImGui.Columns(1, "", false);
                            var pop = true;

                            if (!ImGui.BeginPopupModal(formattableString, ref pop,
                                    ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize)) continue;
                            var x = 0;

                            foreach (var name in _renamedAllStashNames)
                            {
                                x++;

                                if (ImGui.Button($"{name}", new Vector2N(100, 20)))
                                {
                                    indexNode.Value = name;
                                    OnSettingsStashNameChanged(indexNode, name);
                                    ImGui.CloseCurrentPopup();
                                }

                                if (x % 10 != 0)
                                    ImGui.SameLine();
                            }

                            ImGui.Spacing();
                            if (ImGui.Button("Close", new Vector2N(100, 20)))
                                ImGui.CloseCurrentPopup();

                            ImGui.EndPopup();
                        }
                        else
                        {
                            indexNode = new ListIndexNode { Value = "Ignore", Index = -1 };
                        }
                };
        }

        private Vector2 CalculateClickPosition(InventSlotItem inventSlotItem)
        {
            var inventoryPanelRectF = GameController.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory]
                .GetClientRect();

            var cellWidth = inventoryPanelRectF.Width / 12;
            var cellHeight = inventoryPanelRectF.Height / 5;

            Vector2 baseClickPosition = new Vector2(
                inventoryPanelRectF.Location.X + (cellWidth / 2) + (inventSlotItem.PosX * cellWidth),
                inventoryPanelRectF.Location.Y + (cellHeight / 2) + (inventSlotItem.PosY * cellHeight)
            );

            float randomXOffset = Random.Shared.Next(-10, 11);
            float randomYOffset = Random.Shared.Next(-10, 11);

            Vector2 randomizedClickPosition = new Vector2(
                baseClickPosition.X + randomXOffset,
                baseClickPosition.Y + randomYOffset
            );

            return randomizedClickPosition;
        }

        private bool CheckIgnoreCells(InventSlotItem inventItem)
        {
            var inventPosX = inventItem.PosX;
            var inventPosY = inventItem.PosY;

            if (inventPosX is < 0 or >= 12) return true;

            if (inventPosY is < 0 or >= 5) return true;

            return Settings.IgnoredCells[inventPosY, inventPosX] != 0;
        }

        private FilterResult CheckFilters(ItemData itemData, Vector2 clickPosition)
        {
            foreach (var filter in _currentFilter)
            {
                foreach (var subFilter in filter.Filters)
                {
                    try
                    {
                        if (!subFilter.AllowProcess) continue;

                        if (filter.CompareItem(itemData, subFilter.CompiledQuery))
                            return new FilterResult(subFilter, itemData, clickPosition);
                    }
                    catch (Exception e)
                    {
                        DebugWindow.LogError($"Filters Error: {e}");
                    }
                }
            }

            return null;
        }

        private int GetIndexOfCurrentVisibleTab()
        {
            return GameController.Game.IngameState.IngameUi.StashElement.IndexVisibleStash;
        }

        private InventoryType GetTypeOfCurrentVisibleStash()
        {
            var stashPanelVisibleStash = GameController.Game.IngameState.IngameUi?.StashElement?.VisibleStash;
            return stashPanelVisibleStash?.InvType ?? InventoryType.InvalidInventory;
        }

        private int GetInventIndexByStashName(string name)
        {
            var index = _renamedAllStashNames.IndexOf(name);
            if (index != -1) index--;

            return index;
        }

        private List<string> _renamedAllStashNames;

        private void UpdateStashNames(ICollection<string> newNames)
        {
            Settings.AllStashNames = newNames.ToList();

            if (newNames.Count < 4)
            {
                LogError($"{Name}: Cannot parse Stash Tab names.");
                return;
            }

            _renamedAllStashNames = new List<string> { "Ignore" };
            var settingsAllStashNames = Settings.AllStashNames;

            for (var i = 0; i < settingsAllStashNames.Count; i++)
            {
                var realStashName = settingsAllStashNames[i];

                if (_renamedAllStashNames.Contains(realStashName))
                {
                    realStashName += " (" + i + ")";
                }

                _renamedAllStashNames.Add(realStashName ?? "%NULL%");
            }

            Settings.AllStashNames.Insert(0, "Ignore");

            foreach (var lOption in _settingsListNodes)
                try
                {
                    lOption.SetListValues(_renamedAllStashNames);
                    var inventoryIndex = GetInventIndexByStashName(lOption.Value);

                    if (inventoryIndex == -1)
                    {
                        if (lOption.Index != -1)
                        {
                            if (lOption.Index + 1 >= _renamedAllStashNames.Count)
                            {
                                lOption.Index = -1;
                                lOption.Value = _renamedAllStashNames[0];
                            }
                            else
                            {
                                lOption.Value = _renamedAllStashNames[lOption.Index + 1];
                            }
                        }
                        else
                        {
                            lOption.Value =
                                _renamedAllStashNames[0];
                        }
                    }
                    else
                    {
                        lOption.Index = inventoryIndex;
                        lOption.Value = _renamedAllStashNames[inventoryIndex + 1];
                    }
                }
                catch (Exception e)
                {
                    DebugWindow.LogError($"{Name}: UpdateStashNames _settingsListNodes: {e}");
                }

            GenerateMenu();
        }

        private void OnSettingsStashNameChanged(ListIndexNode node, string newValue)
        {
            node.Index = GetInventIndexByStashName(newValue);
        }
    }
}