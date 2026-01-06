using UnityEngine;
using UnityEngine.InputSystem;
using VoxelEngine.Core;
using VoxelEngine.Core.Serialization;
using System.IO;

namespace VoxelEngine.Core.Serialization
{
    public class VoxelSaveLoadController : MonoBehaviour
    {
        public string defaultSaveName = "world_save.vxvol";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInitialize()
        {
            if (FindFirstObjectByType<VoxelSaveLoadController>() == null)
            {
                var go = new GameObject("VoxelSaveLoadController");
                go.AddComponent<VoxelSaveLoadController>();
                DontDestroyOnLoad(go);
                Debug.Log("VoxelSaveLoadController auto-initialized.");
            }
        }

        private InputSystem_Actions playerControls;
        private void Awake()
        {
            playerControls = new InputSystem_Actions();
            playerControls.Player.Save.performed += _ => Save();
            playerControls.Player.Load.performed += _ => Load();
        }

        private void OnEnable()
        {
            playerControls.Player.Enable();
        }

        private void OnDisable()
        {
            playerControls.Player.Disable();
        }

        public void Save()
        {
            var volumes = VoxelVolumeRegistry.Volumes;
            if (volumes.Count > 0)
            {
                // For now, just save the first volume. 
                // In a multi-volume setup, we'd need a way to identify them.
                var volume = volumes[0];
                string path = Application.persistentDataPath + '/' + defaultSaveName;
                Debug.Log($"Saving to {path}...");
                volume.Save(path, (success) => 
                {
                    if (success) Debug.Log("Save Successful!");
                    else Debug.LogError("Save Failed!");
                });
            }
            else
            {
                Debug.LogWarning("No VoxelVolume found to save.");
            }
        }

        public void Load()
        {
            var volumes = VoxelVolumeRegistry.Volumes;
            if (volumes.Count > 0)
            {
                var volume = volumes[0];
                string path = Path.Combine(Application.persistentDataPath, defaultSaveName);
                if (File.Exists(path))
                {
                    Debug.Log($"Loading from {path}...");
                    volume.Load(path);
                }
                else
                {
                    Debug.LogWarning($"Save file not found at {path}");
                }
            }
            else
            {
                Debug.LogWarning("No VoxelVolume found to load into.");
            }
        }
    }
}
