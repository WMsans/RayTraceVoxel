using UnityEngine;

namespace VoxelEngine.Core.Testing
{
    public class DynamicSphereSDFShooter : MonoBehaviour
    {
        [Header("Prefab Settings")]
        [Tooltip("Prefab to spawn. Must have Rigidbody and InteractiveSphereSDF components.")]
        public GameObject spherePrefab;

        [Header("Shooting Settings")]
        [Tooltip("Force applied to the spawned sphere.")]
        public float shootForce = 20f;

        [Tooltip("Shots per second.")]
        public float fireRate = 2f;

        [Tooltip("Offset distance from camera to spawn the sphere (to avoid collision with player).")]
        public float spawnOffset = 1.5f;

        private float lastFireTime;
        public Transform playerCamera;

        private void Start()
        {
            if (playerCamera == null)
            {
                Debug.LogError("DynamicSphereSDFShooter: No main camera found!");
                enabled = false;
            }
        }

        private void Update()
        {
            if (spherePrefab == null) return;

            if (CanFire())
            {
                Shoot();
            }
        }

        private bool CanFire()
        {
            return Time.time >= lastFireTime + (1f / fireRate);
        }

        private void Shoot()
        {
            lastFireTime = Time.time;

            Vector3 spawnPosition = playerCamera.transform.position + playerCamera.transform.forward * spawnOffset;
            Quaternion spawnRotation = playerCamera.transform.rotation;

            GameObject spawnedSphere = Instantiate(spherePrefab, spawnPosition, spawnRotation);

            Rigidbody rb = spawnedSphere.GetComponent<Rigidbody>();
            if (rb == null)
            {
                Debug.LogWarning("DynamicSphereSDFShooter: Spawned prefab missing Rigidbody component!");
                return;
            }

            InteractiveSphereSDF sdf = spawnedSphere.GetComponent<InteractiveSphereSDF>();
            if (sdf == null)
            {
                Debug.LogWarning("DynamicSphereSDFShooter: Spawned prefab missing InteractiveSphereSDF component!");
            }

            Vector3 shootDirection = playerCamera.transform.forward;
            rb.AddForce(shootDirection * shootForce, ForceMode.Impulse);
        }
    }
}
