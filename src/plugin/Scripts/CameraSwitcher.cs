using Assets.Scripts.Actors.Enemies;
using OmniBonk.Helpers;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using TMPro;
using UnityEngine;

namespace OmniBonk.Scripts
{
    public class CameraState
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    public class CameraSwitcher : MonoBehaviour
    {
        private CameraState? originalCameraState;

        private float cameraDistance = 10f;
        private float cameraRadius = 0.3f;
        private float currentZ = 10f;
        private float smoothSpeed = 30f;
        private float positionSmoothSpeed = 0.03f;

        private Transform targetTransform = null;
        private string targetPlayerName = "";
        private int targetIndex = -1;
        private bool isFollowingTarget = false;

        private float yaw = 0f;
        private float pitch = 10f;

        private Vector3 lastPlayerRotation = Vector3.zero;

        private Vector3 smoothedTargetPosition = Vector3.zero;

        private GameObject deathMessageUI;
        private GameObject spectatorInfoUI;
        private TextMeshProUGUI deathMessageText;
        private TextMeshProUGUI spectatorNameText;
        private TextMeshProUGUI spectatorHintText;

        public bool IsFollowingTarget => isFollowingTarget;

        private IPlayerManagerService playerManager;
        private bool isUIInitialized = false;

        private bool wasLeftClickPressed = false;
        private bool wasRightClickPressed = false;

        private void Awake()
        {
            playerManager = Plugin.Services.GetService<IPlayerManagerService>();
        }

        private void CreateUI()
        {
            var canvasParent = UiManager.Instance.encounterWindows.transform;

            deathMessageUI = new GameObject("DeathMessage");
            deathMessageUI.transform.SetParent(canvasParent, false);

            var deathRect = deathMessageUI.AddComponent<RectTransform>();
            deathRect.anchorMin = new Vector2(0.5f, 1f);
            deathRect.anchorMax = new Vector2(0.5f, 1f);
            deathRect.pivot = new Vector2(0.5f, 1f);
            deathRect.anchoredPosition = new Vector2(0, -50);
            deathRect.sizeDelta = new Vector2(800, 100);

            deathMessageText = deathMessageUI.AddComponent<TextMeshProUGUI>();
            deathMessageText.text = "You are dead!";
            deathMessageText.fontSize = 72;
            deathMessageText.fontStyle = FontStyles.Bold;
            deathMessageText.color = Color.red;
            deathMessageText.alignment = TextAlignmentOptions.Center;
            deathMessageUI.SetActive(false);

            spectatorInfoUI = new GameObject("SpectatorInfo");
            spectatorInfoUI.transform.SetParent(canvasParent, false);

            var spectatorRect = spectatorInfoUI.AddComponent<RectTransform>();
            spectatorRect.anchorMin = new Vector2(0.5f, 0f);
            spectatorRect.anchorMax = new Vector2(0.5f, 0f);
            spectatorRect.pivot = new Vector2(0.5f, 0f);
            spectatorRect.anchoredPosition = Vector2.zero;
            spectatorRect.sizeDelta = new Vector2(800, 120);

            GameObject nameTextObj = new GameObject("PlayerName");
            nameTextObj.transform.SetParent(spectatorInfoUI.transform, false);

            var nameRect = nameTextObj.AddComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1f);
            nameRect.anchorMax = new Vector2(1f, 1f);
            nameRect.pivot = new Vector2(0.5f, 1f);
            nameRect.anchoredPosition = new Vector2(0, -20);
            nameRect.sizeDelta = new Vector2(0, 50);

            spectatorNameText = nameTextObj.AddComponent<TextMeshProUGUI>();
            spectatorNameText.fontSize = 36;
            spectatorNameText.fontStyle = FontStyles.Bold;
            spectatorNameText.color = Color.white;
            spectatorNameText.alignment = TextAlignmentOptions.Center;

            GameObject hintTextObj = new GameObject("Hint");
            hintTextObj.transform.SetParent(spectatorInfoUI.transform, false);

            var hintRect = hintTextObj.AddComponent<RectTransform>();
            hintRect.anchorMin = new Vector2(0, 1f);
            hintRect.anchorMax = new Vector2(1f, 1f);
            hintRect.pivot = new Vector2(0.5f, 1f);
            hintRect.anchoredPosition = new Vector2(0, -75);
            hintRect.sizeDelta = new Vector2(0, 30);

            spectatorHintText = hintTextObj.AddComponent<TextMeshProUGUI>();
            spectatorHintText.text = "Left Click: Previous | Right Click: Next";
            spectatorHintText.fontSize = 24;
            spectatorHintText.color = new Color(1f, 1f, 1f, 0.7f);
            spectatorHintText.alignment = TextAlignmentOptions.Center;

            spectatorInfoUI.SetActive(false);
        }

        private void UpdateUI()
        {
            if (!isUIInitialized)
            {
                CreateUI();
                isUIInitialized = true;
            }

            if (isFollowingTarget)
            {
                deathMessageUI.SetActive(true);
                spectatorInfoUI.SetActive(true);

                int totalPlayers = playerManager.GetAllSpawnedNetPlayers().Count();
                spectatorNameText.text = $"Spectating: {targetPlayerName} ({targetIndex + 1}/{totalPlayers})";
            }
            else
            {
                deathMessageUI.SetActive(false);
                spectatorInfoUI.SetActive(false);
            }
        }

        private void AddRotation(float deltaYaw, float deltaPitch)
        {
            yaw += deltaYaw;
            pitch += deltaPitch;
            pitch = Mathf.Clamp(pitch, -80f, 80f);
        }

        public void UpdateFromPlayerRotation(Vector3 playerRotation)
        {
            float deltaYaw = playerRotation.y - lastPlayerRotation.y;
            float deltaPitch = playerRotation.x - lastPlayerRotation.x;

            if (deltaYaw > 180f) deltaYaw -= 360f;
            if (deltaYaw < -180f) deltaYaw += 360f;
            if (deltaPitch > 180f) deltaPitch -= 360f;
            if (deltaPitch < -180f) deltaPitch += 360f;

            AddRotation(deltaYaw, deltaPitch);

            lastPlayerRotation = playerRotation;
        }

        private void NextPlayer()
        {
            var allNetPlayers = playerManager.GetAllSpawnedNetPlayers().ToList();
            if (allNetPlayers.Count == 0) return;

            targetIndex++;
            if (targetIndex >= allNetPlayers.Count)
            {
                targetIndex = 0;
            }

            SwitchToTarget(allNetPlayers[targetIndex].ConnectionId);
        }

        private void PreviousPlayer()
        {
            var allNetPlayers = playerManager.GetAllSpawnedNetPlayers().ToList();
            if (allNetPlayers.Count == 0) return;

            targetIndex--;
            if (targetIndex < 0)
            {
                targetIndex = allNetPlayers.Count - 1;
            }

            SwitchToTarget(allNetPlayers[targetIndex].ConnectionId);
        }

        public void SwitchToTarget(uint targetId)
        {
            var allNetPlayers = playerManager.GetAllSpawnedNetPlayers().ToList();
            var target = allNetPlayers.FirstOrDefault(p => p.ConnectionId == targetId);

            // FIX P2-1: resolve the target before disabling the camera; it may already be gone.
            if (target == null || target.Model == null) // Unity's overloaded == also catches a destroyed object
            {
                Plugin.Log.LogWarning($"Cannot spectate player {targetId}: their NetPlayer is missing or already destroyed.");
                return;
            }

            SaveOriginalCamera();

            var playerCam = GameManager.Instance?.playerCamera;
            if (playerCam != null && playerCam.enabled)
            {
                playerCam.enabled = false;
            }

            targetIndex = allNetPlayers.IndexOf(target);

            var playerModel = playerManager.GetAllPlayersExceptLocal().FirstOrDefault(p => p.ConnectionId == target.ConnectionId);
            if (playerModel != null)
            {
                targetPlayerName = playerModel.Name;
            }

            targetTransform = target.Model.transform;
            isFollowingTarget = true;

            if (GameManager.Instance?.playerCamera?.camera != null)
            {
                var cameraEuler = GameManager.Instance.playerCamera.camera.transform.eulerAngles;
                yaw = cameraEuler.y;
                pitch = cameraEuler.x;
                if (pitch > 180f) pitch -= 360f;
            }
            else
            {
                yaw = 0f;
                pitch = 10f;
            }

            currentZ = cameraDistance;
            smoothedTargetPosition = Vector3.zero;

            UpdateUI();
        }

        public void ResetToLocalPlayer()
        {
            RestoreOriginalCamera();

            var playerCam = GameManager.Instance?.playerCamera;
            if (playerCam != null)
            {
                playerCam.enabled = true;
            }

            isFollowingTarget = false;
            targetTransform = null;
            targetPlayerName = "";
            targetIndex = -1;

            lastPlayerRotation = Vector3.zero;

            Reset();
        }

        public Transform GetCurrentTarget()
        {
            return targetTransform;
        }

        private void Reset()
        {
            isFollowingTarget = false;
            targetTransform = null;
            targetPlayerName = "";
            targetIndex = -1;
            lastPlayerRotation = Vector3.zero;
            smoothedTargetPosition = Vector3.zero;
            currentZ = cameraDistance;
            deathMessageText = null;
            spectatorNameText = null;
            spectatorHintText = null;
            isUIInitialized = false;
            Destroy(deathMessageUI);
            Destroy(spectatorInfoUI);
        }

        public void StopFollowing()
        {
            isFollowingTarget = false;
            targetTransform = null;
            targetPlayerName = "";
            targetIndex = -1;
            lastPlayerRotation = Vector3.zero;

            UpdateUI();
        }

        /// <summary>Camera collision distance, approximating the game's own function.</summary>
        private float GetSafeDistance(Vector3 targetPosition, Vector3 cameraDirection, float desiredDistance)
        {
            Ray ray = new(targetPosition, cameraDirection);
            int layerMask = ~GameManager.Instance.whatIsPlayer;

            RaycastHit[] hits = Il2CppFindHelper.RuntimeSphereCastAll(
                ray,
                cameraRadius,
                desiredDistance,
                layerMask
            );

            float closestDistance = desiredDistance;

            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;

                GameObject obj = hit.collider.gameObject;

                if (obj.CompareTag("MainCamera")) continue;
                if (obj.CompareTag("Player")) continue;
                if (obj.CompareTag("CameraFade")) continue;
                if (obj.CompareTag("CameraIgnore")) continue;
                if (obj.CompareTag("Ignore")) continue;
                if (obj.CompareTag("Interactable")) continue;
                if (obj.GetComponentInChildren<Enemy>() != null) continue;
                if (hit.collider.isTrigger) continue;

                // e.g. the shield pickup.
                if (targetTransform != null && obj.transform.IsChildOf(targetTransform))
                {
                    continue;
                }

                if (hit.distance < closestDistance)
                {
                    closestDistance = hit.distance;
                }
            }

            return closestDistance;
        }

        /// <summary>Switches to another player, or back to the local player if none remain.</summary>
        private void RecoverFromDestroyedTarget()
        {
            var remaining = playerManager.GetAllSpawnedNetPlayers()
                .Where(p => p != null && p.Model != null)
                .ToList();

            if (remaining.Count == 0)
            {
                Plugin.Log.LogWarning("Spectated player is gone and no other player is left to follow; returning the camera to the local player.");
                ResetToLocalPlayer();
                return;
            }

            Plugin.Log.LogWarning("Spectated player is gone; switching the spectator camera to another player.");
            SwitchToTarget(remaining[0].ConnectionId);

            // SwitchToTarget may bail too; avoid re-warning every frame.
            if (targetTransform == null)
            {
                ResetToLocalPlayer();
            }
        }

        public void Update()
        {
            if (!isFollowingTarget) return;

            // FIX P2-1: the spectated peer can be destroyed with no notification. Recovered here
            // because two racing disconnect paths remove players.
            if (targetTransform == null) // Unity's overloaded == also catches a destroyed object
            {
                RecoverFromDestroyedTarget();
                return;
            }

            bool isLeftClickPressed = BepInEx.Unity.IL2CPP.UnityEngine.Input.GetKeyInt((BepInEx.Unity.IL2CPP.UnityEngine.KeyCode)KeyCode.Mouse0);
            bool isRightClickPressed = BepInEx.Unity.IL2CPP.UnityEngine.Input.GetKeyInt((BepInEx.Unity.IL2CPP.UnityEngine.KeyCode)KeyCode.Mouse1);

            if (isLeftClickPressed && !wasLeftClickPressed)
            {
                PreviousPlayer();
            }
            else if (isRightClickPressed && !wasRightClickPressed)
            {
                NextPlayer();
            }

            wasLeftClickPressed = isLeftClickPressed;
            wasRightClickPressed = isRightClickPressed;
        }

        public void LateUpdate()
        {
            if (!isFollowingTarget || targetTransform == null)
            {
                return;
            }

            if (GameManager.Instance?.playerCamera?.camera == null)
            {
                return;
            }

            var cameraTransform = GameManager.Instance.playerCamera.camera.transform;

            Vector3 rawTargetPosition = targetTransform.position;
            rawTargetPosition.y += Plugin.PLAYER_FEET_OFFSET_Y + 2.5f;

            if (smoothedTargetPosition == Vector3.zero)
            {
                smoothedTargetPosition = rawTargetPosition;
            }
            else
            {
                float t = Mathf.Clamp01(Time.deltaTime / positionSmoothSpeed);
                smoothedTargetPosition = Vector3.Lerp(smoothedTargetPosition, rawTargetPosition, t);
            }

            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 cameraDirection = rotation * Vector3.back;

            float safeDistance = GetSafeDistance(smoothedTargetPosition, cameraDirection, cameraDistance);

            float speed = Mathf.Clamp01(Time.deltaTime * smoothSpeed);
            currentZ = Mathf.Lerp(currentZ, safeDistance, speed);

            Vector3 finalPosition = smoothedTargetPosition + cameraDirection * currentZ;

            cameraTransform.position = finalPosition;

            cameraTransform.LookAt(smoothedTargetPosition);
        }

        private void SaveOriginalCamera()
        {
            if (originalCameraState != null) return;

            var cam = GameManager.Instance?.playerCamera;
            if (cam == null || cam.camera == null) return;

            originalCameraState = new CameraState
            {
                position = cam.camera.transform.position,
                rotation = cam.camera.transform.rotation,
            };
        }

        private void RestoreOriginalCamera()
        {
            if (originalCameraState == null) return;

            var cam = GameManager.Instance?.playerCamera;
            if (cam == null || cam.camera == null) return;

            var state = originalCameraState;

            cam.camera.transform.position = state.position;
            cam.camera.transform.rotation = state.rotation;

            cam.UpdateZoom();

            originalCameraState = null;
        }
    }
}
