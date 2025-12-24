using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using PluginUtilities;
using BepInEx.Logging;
using RadialUI;
using HideVolumeExtensions.Patches;
using System.Collections.Generic;
using Bounce.Singletons;
using System.Collections.Concurrent;
using System.IO;

namespace HideVolumeExtensions
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(RadialUIPlugin.Guid)]
    [BepInDependency(SetInjectionFlag.Guid)]
    public sealed class HVEPlugin : BaseUnityPlugin
    {
        // constants
        public const string Guid = "org.HF.plugins.DCHV";
        public const string Version = "0.0.0.0";
        private const string Name = "Door Controlled Hide Volumes";

        internal static Harmony harmony;

        internal static ManualLogSource _logger;

        internal static ConfigEntry<bool> HideOnClose;

        internal static string LocalHidden;

        public static void DoPatching()
        {
            harmony = new Harmony(Guid);
            harmony.PatchAll();
            _logger.LogInfo($"{Name}: Patched.");
        }

        private static void DoConfig(ConfigFile config)
        {
            HideOnClose = config.Bind("Door Volume Linking", "Hide On Close", false);
        }

        public static void UnPatch()
        {
            harmony.UnpatchSelf();
            _logger.LogDebug($"{Name}: UnPatched.");
        }

        private void Awake()
        {
            _logger = Logger;
            LocalHidden = Path.GetDirectoryName(Info.Location)+"/BoardData";
            Directory.CreateDirectory(LocalHidden);

            DoConfig(Config);
            DoPatching();
            _logger.LogInfo($"{Name} is Active.");

            RadialUIPlugin.AddOnHideVolume(Guid + "AddDoor", new MapMenu.ItemArgs
            {
                CloseMenuOnActivate = true,
                Title = "Show on Door Open",
                Action = AddDoorOpen,
            }, CanAddVolume);
            RadialUIPlugin.AddOnHideVolume(Guid + "RemoveDoor", new MapMenu.ItemArgs
            {
                CloseMenuOnActivate = true,
                Title = "Remove show on Door Open",
                Action = RemoveDoorOpen,
            }, CanRemoveVolume);
        }

        internal static ConcurrentStack<HideVolumeItem> HVToDeactivate = new ConcurrentStack<HideVolumeItem>();
        internal static ConcurrentStack<HideVolumeItem> HVToActivate = new ConcurrentStack<HideVolumeItem>();
        
        private void Update()
        {
            if (HVToDeactivate.TryPop(out HideVolumeItem item))
            {
                _logger.LogDebug($"Processing HideVolumeItem: {item.HideVolume.Id}");
                item.ChangeIsActive(false);
                SimpleSingletonBehaviour<HideVolumeManager>.Instance.SetHideVolumeState(item.HideVolume);
            }

            if (HVToActivate.TryPop(out HideVolumeItem item2))
            {
                _logger.LogDebug($"Processing HideVolumeItem: {item2.HideVolume.Id}");
                item2.ChangeIsActive(true);
                SimpleSingletonBehaviour<HideVolumeManager>.Instance.SetHideVolumeState(item2.HideVolume);
            }
        }

        private HideVolumeItem lastItem;

        private bool CanAddVolume(HideVolumeItem item)
        {
            lastItem = item;
            if (string.IsNullOrEmpty(Patches.Patches.selectedDoor) == false)
            {
                string contentId = Patches.Patches.selectedDoor;
                SMMPatch.LoadDoorHideVolumes();
                return !SMMPatch.DoorHideVolumes.ContainsKey(contentId) || !SMMPatch.DoorHideVolumes[contentId].Contains(lastItem.HideVolume.Id.ToString());
            }
            return false;
        }

        private bool CanRemoveVolume(HideVolumeItem item)
        {
            lastItem = item;
            if (string.IsNullOrEmpty(Patches.Patches.selectedDoor) == false)
            {
                string contentId = Patches.Patches.selectedDoor;
                SMMPatch.LoadDoorHideVolumes();
                if (SMMPatch.DoorHideVolumes.ContainsKey(contentId))
                {
                    return SMMPatch.DoorHideVolumes[contentId].Contains(lastItem.HideVolume.Id.ToString());
                }
                return false;
            }
            return false;
        }

        private void AddDoorOpen(MapMenuItem i, object o) {
            if (string.IsNullOrEmpty(Patches.Patches.selectedDoor))
            {
                _logger.LogWarning("No door selected. Please select a door first.");
                return;
            }
            
            SMMPatch.LoadDoorHideVolumes();
            string contentId = Patches.Patches.selectedDoor;
            if (!SMMPatch.DoorHideVolumes.ContainsKey(contentId))
            {
                _logger.LogDebug($"Adding new door hide volume for contentId: {contentId}");
                SMMPatch.DoorHideVolumes[contentId] = new List<string>();
            }
            
            if (SMMPatch.DoorHideVolumes[contentId].Contains(lastItem.HideVolume.Id.ToString()))
            {
                _logger.LogWarning($"Hide volume {lastItem.HideVolume.Id} already exists for door {contentId}. Skipping addition.");
                return;
            }

            SMMPatch.DoorHideVolumes[contentId].Add(lastItem.HideVolume.Id.ToString());
            SMMPatch.SaveDoorHideVolumes();
        }

        private void RemoveDoorOpen(MapMenuItem i, object o)
        {
            if (string.IsNullOrEmpty(Patches.Patches.selectedDoor))
            {
                _logger.LogWarning("No door selected. Please select a door first.");
                return;
            }

            SMMPatch.LoadDoorHideVolumes();
            string contentId = Patches.Patches.selectedDoor;


            if (!SMMPatch.DoorHideVolumes.ContainsKey(contentId))
            {
                return;
            }

            if (SMMPatch.DoorHideVolumes[contentId].Contains(lastItem.HideVolume.Id.ToString()))
            {
                SMMPatch.DoorHideVolumes[contentId].Remove(lastItem.HideVolume.Id.ToString());
            }

            SMMPatch.SaveDoorHideVolumes();
        }
    }
}