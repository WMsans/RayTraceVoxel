using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float verticalSpeed = 3f;

    [Header("Camera Settings")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private float mouseSensitivity = 0.1f;

    [Header("Component References")]
    [SerializeField] private Rigidbody rb;

    private InputSystem_Actions playerControls;
    private Vector2 moveInput;
    private float verticalMoveInput; 
    
    // Rotation state
    private float xRotation = 0f; // Pitch (Camera up/down)
    private float yRotation = 0f; // Yaw (Body left/right)

    private void Awake()
    {
        if (rb == null)
        {
            rb = GetComponent<Rigidbody>();
        }
        
        rb.freezeRotation = true;
        // Ensure the initial rotation is captured so we don't snap to 0
        yRotation = transform.eulerAngles.y;

        playerControls = new InputSystem_Actions();

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        playerControls.Player.Move.performed += ctx => moveInput = ctx.ReadValue<Vector2>();
        playerControls.Player.Move.canceled += ctx => moveInput = Vector2.zero;

        playerControls.Player.Jump.performed += _ => verticalMoveInput += 1f;
        playerControls.Player.Jump.canceled += _ => verticalMoveInput -= 1f;

        playerControls.Player.Crouch.performed += _ => verticalMoveInput -= 1f;
        playerControls.Player.Crouch.canceled += _ => verticalMoveInput += 1f;
    }

    private void OnEnable()
    {
        playerControls.Player.Enable();
    }

    private void OnDisable()
    {
        playerControls.Player.Disable();
    }

    private void Update()
    {
        // Read Input
        Vector2 lookInput = playerControls.Player.Look.ReadValue<Vector2>();
        float mouseX = lookInput.x * mouseSensitivity;
        float mouseY = lookInput.y * mouseSensitivity;

        // --- Pitch (Look Up/Down) ---
        // This is purely visual and local to the camera, so we keep it in Update for smoothness.
        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);
        cameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        
        // --- Yaw (Look Left/Right) ---
        // We only ACCUMULATE the value here. We do not apply it to the Transform yet.
        yRotation += mouseX;
    }

    private void FixedUpdate()
    {
        // --- Apply Physics Rotation ---
        // Using MoveRotation ensures the physics engine is aware of the change and doesn't fight it.
        Quaternion targetRotation = Quaternion.Euler(0f, yRotation, 0f);
        rb.MoveRotation(targetRotation);

        // --- Apply Movement ---
        // Calculate direction based on the NEW target rotation to ensure responsiveness
        Vector3 targetForward = targetRotation * Vector3.forward;
        Vector3 targetRight = targetRotation * Vector3.right;

        Vector3 horizontalVelocity = (targetRight * moveInput.x + targetForward * moveInput.y) * moveSpeed;
        
        rb.linearVelocity = new Vector3(horizontalVelocity.x, verticalMoveInput * verticalSpeed, horizontalVelocity.z);
    }

    private void OnDestroy()
    {
        playerControls?.Dispose();
    }
}