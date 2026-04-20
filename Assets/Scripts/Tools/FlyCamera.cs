using UnityEngine;

/// Attach to the camera. Hold right mouse button to look, WASD/QE to move.
/// Scroll wheel adjusts move speed. Shift multiplies speed by 3.
public class FlyCamera : MonoBehaviour
{
    public float MoveSpeed  = 50f;
    public float LookSpeed  = 2f;
    public float ShiftMultiplier = 3f;

    float yaw;
    float pitch;

    void Start()
    {
        var angles = transform.eulerAngles;
        yaw   = angles.y;
        pitch = angles.x;
    }

    void Update()
    {
        // Scroll to adjust base speed
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0f)
            MoveSpeed = Mathf.Clamp(MoveSpeed * (1f + scroll * 5f), 1f, 10000f);

        // Right mouse held — look
        if (Input.GetMouseButton(1))
        {
            yaw   += Input.GetAxis("Mouse X") * LookSpeed;
            pitch -= Input.GetAxis("Mouse Y") * LookSpeed;
            pitch  = Mathf.Clamp(pitch, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        // WASD + QE move
        float speed = MoveSpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            speed *= ShiftMultiplier;

        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move += transform.forward;
        if (Input.GetKey(KeyCode.S)) move -= transform.forward;
        if (Input.GetKey(KeyCode.D)) move += transform.right;
        if (Input.GetKey(KeyCode.A)) move -= transform.right;
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;

        transform.position += move.normalized * speed;
    }
}
