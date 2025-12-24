using Bounce.ManagedCollections;
using Bounce.Unmanaged;
using Bounce.UnsafeViews;
using DataModel;
using HarmonyLib;
using Newtonsoft.Json;
using Spaghet.Runtime;
using Spaghet.VM;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleSpire.ContentManagement;

namespace HideVolumeExtensions.Patches
{
    [HarmonyPatch(typeof(HideVolumeManager), "AddHideVolume")]
    [HarmonyPatch(typeof(HideVolumeManager), "RemoveHideVolume")]
    [HarmonyPatch(typeof(HideVolumeManager), "OnHideVolumeAdded")]
    [HarmonyPatch(typeof(HideVolumeManager), "OnHideVolumeRemoved")]
    [HarmonyPatch(typeof(HideVolumeManager), "OnHideVolumeStateChanged")]
    internal sealed class HVMPatch
    {
        internal static Dictionary<string,HideVolumeItem> _hideVolumeItems;

        static void Postfix(ref BList<HideVolumeItem> ____hideVolumeItems)
        {
            _hideVolumeItems = ____hideVolumeItems.ToDictionary(hv => hv.HideVolume.Id.ToString());

            HVEPlugin._logger.LogDebug($"HideVolumeManager Patched. Total HideVolumes: {_hideVolumeItems.Count}");
            HVEPlugin._logger.LogDebug($"HideVolumeManager Patched. HideVolumes: {string.Join(", ", _hideVolumeItems.Keys)}");
        }
    }

    [HarmonyPatch(typeof(PlaceableHandle), nameof(PlaceableHandle.Interact))]
    internal sealed class Patches
    {
        internal static string? selectedDoor;
        internal static string? lastDoor;

        static void Prefix(ref PlaceableHandle __instance)
        {
            HVEPlugin._logger.LogInfo($"Prefix Triggr");
            if (!__instance.HasValue || !InternalPackManager.Placeables.TryGetValue(__instance.ContentId, out var item) || !__instance.TryGetPlaceableRef(out var placeableRef))
            {
                return;
            }

            HVEPlugin._logger.LogInfo($"Data: {item.Value.Id}: {placeableRef.Data.WorldOrigin}");
        }

        static void Postfix(ref PlaceableHandle __instance, ref Zone ____zone)
        {
            // Check if the current client is in GM mode,
            // player should not be able to select doors
            if (!LocalClient.IsInGmMode)
                return;

            HVEPlugin._logger.LogDebug("Placeable Interacted with");

            if (!__instance.HasValue || !InternalPackManager.Placeables.TryGetValue(__instance.ContentId, out var item) || !__instance.TryGetPlaceableRef(out var placeableRef))
            {
                return;
            }
            
            int scriptIndex = placeableRef.Data.ScriptIndex;
            if (scriptIndex == -1)
            {
                return;
            }

            ____zone.StateMachineManager.GetInternalState(out var state, out var _, out var _);
            ref PrivateState reference = ref state.ElementReadOnlyRef(scriptIndex);
            ushort executionStateCode = reference.ExecutionState.ExecutionStateCode;
            UnsafeArrayView<Menu.Packed> unsafeArrayView = item.Value.StateMachineScript.GetValue().Menus.TakeView();
            if (executionStateCode >= unsafeArrayView.Length)
            {
                return;
            }

            lastDoor = $"{reference.ScriptId}:{____zone.Coord}";

            RadialUI.Extensions.MapMenuManagerPatch.mapMenu.AddItem(new MapMenu.ItemArgs
            {
                Action = SelectDoor,//delegate { selectedDoor = contentId; HVEPlugin._logger.LogDebug($"Door Selected {contentId.Value.ToString()}"); },
                Title = "Select Door",
                CloseMenuOnActivate = true
            });
        }

        static void SelectDoor(MapMenuItem item, object o)
        {
            HVEPlugin._logger.LogDebug($"Door value {selectedDoor}");
            selectedDoor = lastDoor;
            HVEPlugin._logger.LogDebug($"Door updated to {selectedDoor}");
        }
    }

    [HarmonyPatch(typeof(StateMachineManager), nameof(StateMachineManager.ReceiveMessage))]
    internal sealed class SMMPatch
    {
        static BoardGuid CurrentBoard;
        internal static Dictionary<string, List<string>> DoorHideVolumes;

        internal static void LoadDoorHideVolumes()
        {
            if (CurrentBoard != BoardSessionManager.CurrentBoardInfo.Id)
            {
                CurrentBoard = BoardSessionManager.CurrentBoardInfo.Id;
                try
                {
                    // Check directory exists
                    string filePath = Path.Join(HVEPlugin.LocalHidden, BoardSessionManager.CurrentBoardInfo.Id.ToString());
                    
                    if (File.Exists(filePath))
                    {
                        string result = File.ReadAllText(filePath);
                        DoorHideVolumes = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(result) ?? new Dictionary<string, List<string>>();
                    }
                    else
                    {
                        DoorHideVolumes = new Dictionary<string, List<string>>();
                    }
                }
                catch (System.Exception e)
                {
                    HVEPlugin._logger.LogWarning($"Failed to load door hide volumes: {e.Message}");
                    DoorHideVolumes = new Dictionary<string, List<string>>();
                }
            }
        }

        internal static void SaveDoorHideVolumes()
        {
            // Check directory exists
            string json = JsonConvert.SerializeObject(DoorHideVolumes, Formatting.Indented);
            File.WriteAllText(Path.Join(HVEPlugin.LocalHidden, BoardSessionManager.CurrentBoardInfo.Id.ToString()), json);
        }

        static void Postfix(ref StateMessageV6 msg, ref bool resetLocalTime, ref StateMachineManager __instance)
        {
            string sid = msg.QualifiedScriptId.ScriptId.ToString();

            HVEPlugin._logger.LogDebug($"{sid}:{msg.QualifiedScriptId.ZoneCoord}");
            
            sid = $"{sid}:{msg.QualifiedScriptId.ZoneCoord}";

            LoadDoorHideVolumes();
            if (msg.ExecutionStateCode == 0)
            {
                HVEPlugin._logger.LogDebug($"message state code: {msg.ExecutionStateCode}");

                if (DoorHideVolumes.ContainsKey(sid))
                {
                    HVEPlugin._logger.LogDebug($"Door Hide Volumes found for {sid}");
                    foreach (var volumeId in DoorHideVolumes[sid].Where(volumeId => HVMPatch._hideVolumeItems.ContainsKey(volumeId)))
                    {
                        HVEPlugin._logger.LogDebug($"Processing volume {volumeId} for door {sid}");
                        if (HVMPatch._hideVolumeItems[volumeId].HideVolume.IsActive)
                            HVEPlugin.HVToDeactivate.Push(HVMPatch._hideVolumeItems[volumeId]);
                    }
                }
            }
            else if (msg.ExecutionStateCode == 1 && HVEPlugin.HideOnClose.Value)
            {
                HVEPlugin._logger.LogDebug($"message state code: {msg.ExecutionStateCode}");

                if (DoorHideVolumes.ContainsKey(sid))
                {
                    HVEPlugin._logger.LogDebug($"Door Hide Volumes found for {sid}");
                    foreach (var volumeId in DoorHideVolumes[sid].Where(volumeId => HVMPatch._hideVolumeItems.ContainsKey(volumeId)))
                    {
                        HVEPlugin._logger.LogDebug($"Processing volume {volumeId} for door {sid}");
                        if (HVMPatch._hideVolumeItems[volumeId].HideVolume.IsActive == false)
                            HVEPlugin.HVToActivate.Push(HVMPatch._hideVolumeItems[volumeId]);
                    }
                }
            }
        }
    }
}