using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Allows the avatar to be rotated around the Y axis in VR.
/// Point at the avatar with a near-far interactor, hold the trigger, then sweep
/// the controller left or right. Added programmatically by UIManager.SetupGenie.
/// </summary>
public class AvatarRotator : MonoBehaviour
{
    [Tooltip("Multiplier applied to the controller's horizontal sweep angle.")]
    public float rotationSensitivity = 3.0f;

    [Tooltip("Height of the auto-generated CapsuleCollider (metres). Adjust to match avatar height.")]
    public float colliderHeight = 1.8f;

    [Tooltip("Radius of the auto-generated CapsuleCollider (metres).")]
    public float colliderRadius = 0.3f;

    [Tooltip("How far above the top of the collider the gizmo floats (metres).")]
    public float gizmoOffset = 0.2f;

    [Tooltip("Optional: assign a TMP font asset that includes ↺ ↻ symbols. Leave empty to use the TMP default.")]
    public TMP_FontAsset gizmoFont;

    private XRSimpleInteractable _interactable;
    private IXRHoverInteractor _hoveringInteractor;
    private NearFarInteractor _nearFarInteractor;
    private float _startControllerYaw;
    private float _startAvatarYaw;
    private bool _isRotating;
    private bool _isHovering;
    private GameObject _rotationGizmo;
    private TextMeshPro _gizmoTmp;

    void Awake()
    {
        EnsureCollider();
        CreateRotationGizmo();

        _interactable = GetComponent<XRSimpleInteractable>();
        if (_interactable == null)
            _interactable = gameObject.AddComponent<XRSimpleInteractable>();

        _interactable.hoverEntered.AddListener(OnHoverEntered);
        _interactable.hoverExited.AddListener(OnHoverExited);
    }

    void Start()
    {
        if (gizmoFont != null)
            _gizmoTmp.font = gizmoFont;
    }

    void OnDestroy()
    {
        if (_interactable != null)
        {
            _interactable.hoverEntered.RemoveListener(OnHoverEntered);
            _interactable.hoverExited.RemoveListener(OnHoverExited);
        }

        if (_rotationGizmo != null)
            Destroy(_rotationGizmo);
    }

    private void OnHoverEntered(HoverEnterEventArgs args)
    {
        if (_hoveringInteractor != null) return; // already tracking one controller

        NearFarInteractor nfi = args.interactorObject as NearFarInteractor;
        if (nfi == null) return;

        _hoveringInteractor = args.interactorObject;
        _nearFarInteractor = nfi;
        _isHovering = true;
        _rotationGizmo.SetActive(true);
        TryCancelHaptics(nfi);
    }

    private void OnHoverExited(HoverExitEventArgs args)
    {
        if (args.interactorObject != _hoveringInteractor) return;

        TryCancelHaptics(_nearFarInteractor);
        _isHovering = false;

        // If the trigger is held, keep rotating even though the ray left the avatar.
        // Update() will hide the gizmo and clear the interactor once trigger releases.
        if (!_isRotating)
        {
            _hoveringInteractor = null;
            _nearFarInteractor = null;
            _rotationGizmo.SetActive(false);
        }
    }

    private void TryCancelHaptics(NearFarInteractor nfi)
    {
        HapticImpulsePlayer hapticPlayer = nfi.GetComponentInParent<HapticImpulsePlayer>();
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0f, 0.05f);
    }

    void Update()
    {
        // Keep gizmo pinned above the avatar and facing the player each frame
        if (_rotationGizmo.activeSelf)
        {
            _rotationGizmo.transform.position = transform.position + Vector3.up * (colliderHeight + gizmoOffset);

            if (Camera.main != null)
            {
                Vector3 dir = Camera.main.transform.position - _rotationGizmo.transform.position;
                if (dir != Vector3.zero)
                    _rotationGizmo.transform.rotation = Quaternion.LookRotation(dir);
            }
        }

        if (_nearFarInteractor == null) return;

        bool triggerHeld = _nearFarInteractor.activateInput.ReadIsPerformed();

        if (triggerHeld && !_isRotating)
        {
            // Record reference point the moment the trigger is depressed
            _startControllerYaw = GetControllerYaw(_nearFarInteractor);
            _startAvatarYaw = transform.eulerAngles.y;
            _isRotating = true;
        }
        else if (!triggerHeld && _isRotating)
        {
            _isRotating = false;
            // Only free the interactor if the ray has already left the avatar.
            // If still hovering, keep refs alive so the next trigger press works.
            if (!_isHovering)
            {
                _hoveringInteractor = null;
                _nearFarInteractor = null;
                _rotationGizmo.SetActive(false);
            }
        }

        if (_isRotating)
        {
            float currentYaw = GetControllerYaw(_nearFarInteractor);
            // Negate delta so pointing right rotates clockwise from the user's perspective
            float delta = Mathf.DeltaAngle(_startControllerYaw, currentYaw);
            float newY = _startAvatarYaw - delta * rotationSensitivity;
            transform.rotation = Quaternion.Euler(0f, newY, 0f);
        }
    }

    // Projects the controller's forward vector onto the horizontal plane and returns
    // the signed angle from world +Z, giving us a yaw value to diff against.
    private float GetControllerYaw(NearFarInteractor interactor)
    {
        Vector3 forward = interactor.transform.forward;
        return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }

    private void CreateRotationGizmo()
    {
        _rotationGizmo = new GameObject("RotationGizmo");
        // Not parented to the avatar — position is tracked manually in Update
        // so the gizmo doesn't orbit when the avatar rotates.
        _rotationGizmo.transform.position = transform.position + Vector3.up * (colliderHeight + gizmoOffset);

        _gizmoTmp = _rotationGizmo.AddComponent<TextMeshPro>();
        _gizmoTmp.text = "↺";
        _gizmoTmp.fontSize = 3f;
        _gizmoTmp.alignment = TextAlignmentOptions.Center;
        _gizmoTmp.color = Color.white;
        _gizmoTmp.textWrappingMode = TextWrappingModes.NoWrap;
        // Font is applied in Start() so external assignments made after
        // AddComponent (which triggers Awake immediately) are not missed.

        // Size the rect to fit the two symbols side by side
        RectTransform rt = _rotationGizmo.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(4f, 1f);

        _rotationGizmo.SetActive(false);
    }

    // Adds a CapsuleCollider if the avatar has no Collider — required for the ray
    // interactor to hit the object.
    private void EnsureCollider()
    {
        if (GetComponent<Collider>() != null) return;

        CapsuleCollider cap = gameObject.AddComponent<CapsuleCollider>();
        cap.height = colliderHeight;
        cap.radius = colliderRadius;
        cap.center = new Vector3(0f, colliderHeight * 0.5f, 0f);
        cap.direction = 1; // Y-axis
    }
}
